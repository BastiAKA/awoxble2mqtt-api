using System.Buffers.Binary;
using AwoxController.Ble;

// btsnoop (HCI) decoder for AwoX/Eglo mesh: parses an Android btsnoop_hci.log, reconstructs each
// connection's AwoX session key from the login handshake, and decrypts every status notification.
//
// Usage: dotnet run --project tools/BtsnoopDecode -- <btsnoop_hci.log> [meshName] [meshPassword]

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: BtsnoopDecode <file> [meshName] [meshPassword]");
    return 2;
}

var path = args[0];
var meshName = args.Length > 1 ? args[1] : "Y4kl7frn";
var meshPassword = args.Length > 2 ? args[2] : "0dgq5qfC";

var data = File.ReadAllBytes(path);
if (data.Length < 16 || System.Text.Encoding.ASCII.GetString(data, 0, 7) != "btsnoop")
{
    Console.Error.WriteLine("Not a btsnoop file.");
    return 2;
}

// --- per-connection state -----------------------------------------------------------------------
var connMac = new Dictionary<int, string>();              // connHandle -> peer MAC
var connSessionRandom = new Dictionary<int, byte[]>();    // from the 0x0c pair write
var connKey = new Dictionary<int, byte[]>();              // derived session key
var reasm = new Dictionary<int, List<byte>>();            // L2CAP reassembly per connHandle
var readReqHandle = new Dictionary<int, ushort>();        // last ATT read request handle per conn

var notifCount = 0;
var distinct = new HashSet<string>();
var attOps = new SortedDictionary<string, int>();        // "op 0xNN -> handle 0xNNNN" summary

int pos = 16;
while (pos + 24 <= data.Length)
{
    // btsnoop record header (big-endian)
    int inclLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4, 4));
    pos += 24;
    if (pos + inclLen > data.Length || inclLen <= 0) break;
    var pkt = data.AsSpan(pos, inclLen);
    pos += inclLen;

    if (pkt.Length < 1) continue;
    var h4 = pkt[0];
    if (h4 == 0x04) { ParseEvent(pkt[1..]); continue; }
    if (h4 != 0x02) continue; // only ACL data

    // HCI ACL header (little-endian)
    if (pkt.Length < 5) continue;
    var handleFlags = BinaryPrimitives.ReadUInt16LittleEndian(pkt.Slice(1, 2));
    int conn = handleFlags & 0x0FFF;
    int pb = (handleFlags >> 12) & 0x3;
    int aclLen = BinaryPrimitives.ReadUInt16LittleEndian(pkt.Slice(3, 2));
    var aclData = pkt.Slice(5, Math.Min(aclLen, pkt.Length - 5));

    if (pb is 0x02 or 0x00)
    {
        reasm[conn] = new List<byte>();
        reasm[conn].AddRange(aclData.ToArray());
    }
    else if (pb == 0x01 && reasm.TryGetValue(conn, out var buf))
    {
        buf.AddRange(aclData.ToArray());
    }
    else continue;

    var frame = reasm[conn];
    if (frame.Count < 4) continue;
    int l2capLen = frame[0] | (frame[1] << 8);
    if (frame.Count < 4 + l2capLen) continue; // wait for more fragments
    int cid = frame[2] | (frame[3] << 8);
    var att = frame.GetRange(4, l2capLen).ToArray();
    reasm.Remove(conn);

    if (cid != 0x0004 || att.Length < 1) continue;
    ParseAtt(conn, att);
}

Console.WriteLine();
Console.WriteLine("=== connection -> peer MAC ===");
foreach (var kv in connMac) Console.WriteLine($"  conn=0x{kv.Key:X3} -> {kv.Value}");
Console.WriteLine();
Console.WriteLine("=== ATT ops by handle (handshake overview) ===");
foreach (var kv in attOps) Console.WriteLine($"  {kv.Key}  x{kv.Value}");
Console.WriteLine();
Console.WriteLine($"=== {notifCount} status notification(s); {distinct.Count} distinct (meshId, block) ===");
foreach (var d in distinct.OrderBy(x => x)) Console.WriteLine("  " + d);
return 0;

// ------------------------------------------------------------------------------------------------
void ParseAtt(int conn, byte[] att)
{
    var op = att[0];

    // Handshake visibility: record every write/notify by handle, and flag CCCD writes + foreign
    // notification handles (anything other than the 0x0012 status char).
    if ((op is 0x12 or 0x52 or 0x1b or 0x0a or 0x0d) && att.Length >= 3)
    {
        var h = (ushort)(att[1] | (att[2] << 8));
        var k = $"conn=0x{conn:X3}  op 0x{op:X2} -> handle 0x{h:X4}";
        attOps[k] = attOps.TryGetValue(k, out var c) ? c + 1 : 1;
        var body = att[3..];
        if ((op is 0x12 or 0x52) && body.Length == 2 && (body[0] is 0x01 or 0x02) && body[1] == 0x00)
            Log(conn, $"*** CCCD/descriptor WRITE handle=0x{h:X4} value={Hex(body)} (notify/indicate enable) ***");
        if ((op is 0x12 or 0x52) && body.Length is >= 1 and <= 3)
            Log(conn, $"*** short WRITE handle=0x{h:X4} value={Hex(body)} ***");
        if (op == 0x1b && h != 0x0012)
            Log(conn, $"*** NOTIFY on FOREIGN handle 0x{h:X4} value={Hex(body)} ***");
    }

    switch (op)
    {
        case 0x0a when att.Length >= 3: // Read Request -> remember handle for the response
            readReqHandle[conn] = (ushort)(att[1] | (att[2] << 8));
            break;

        case 0x12 or 0x52 when att.Length >= 3: // Write Request / Command
        {
            var handle = (ushort)(att[1] | (att[2] << 8));
            var val = att[3..];
            if (val.Length >= 17 && val[0] == 0x0C) // pair packet -> session random
            {
                connSessionRandom[conn] = val[1..9];
                Log(conn, $"LOGIN write (pair) handle=0x{handle:X4} sessRand={Hex(val[1..9])}");
            }
            else if (val.Length == 17 && val[0] != 0x0C && connKey.TryGetValue(conn, out var ck))
            {
                // Command packet: flag | AES(block), block = [crc][len][destBE(2)][opcode+data].
                var cb = AwoxMeshProtocol.DecryptZigbee(ck, val.AsSpan(1, 16));
                int len = cb[1];
                var dest = (ushort)((cb[2] << 8) | cb[3]);
                var cmd = len >= 2 && len <= 14 ? Hex(cb.AsSpan(4, len - 2)) : "(len?)";
                Log(conn, $"CMD flag=0x{val[0]:X2} dest=0x{dest:X4} op+data={cmd}  block={Hex(cb)}");
            }
            break;
        }

        case 0x0b: // Read Response (handle is from the preceding Read Request)
        {
            var val = att[1..];
            var rh = readReqHandle.TryGetValue(conn, out var hh) ? hh : (ushort)0;
            var isPairChar = rh is 0x001E or 0x001B; // some models pair via 0x001B instead of 0x001E
            if (val.Length >= 9 && val[0] == 0x0D && isPairChar && connSessionRandom.TryGetValue(conn, out var sr))
            {
                var key = AwoxMeshProtocol.MakeSessionKey(meshName, meshPassword, sr, val[1..9]);
                connKey[conn] = key;
                Log(conn, $"LOGIN reply (h=0x{rh:X4}) devRand={Hex(val[1..9])} -> key={Hex(key)}");
            }
            else if (!isPairChar)
            {
                var dec = val.Length >= 16 && connKey.TryGetValue(conn, out var k) ? "  dec=" + Hex(AwoxMeshProtocol.DecryptZigbee(k, val.AsSpan(0, 16))) : "";
                Log(conn, $"READ RESP handle=0x{rh:X4} value={Hex(val)}{dec}");
            }
            break;
        }

        case 0x1b when att.Length >= 3: // Handle Value Notification
        {
            var handle = (ushort)(att[1] | (att[2] << 8));
            var val = att[3..];
            DecodeStatus(conn, handle, val);
            break;
        }
    }
}

void DecodeStatus(int conn, ushort handle, byte[] val)
{
    notifCount++;
    var mac = connMac.TryGetValue(conn, out var m) ? m : "??:??:??:??:??:??";
    if (val.Length >= 17 && connKey.TryGetValue(conn, out var key))
    {
        var block = AwoxMeshProtocol.DecryptZigbee(key, val.AsSpan(1, 16));
        var meshId = block.Length >= 2 ? (ushort)(block[0] | (block[1] << 8)) : 0;
        var state = block.Length >= 6 ? Hex(block[2..6]) : "";
        Log(conn, $"NOTIFY h=0x{handle:X4} flag=0x{val[0]:X2} src[{mac}] meshId=0x{meshId:X4}({meshId}) state={state} block={Hex(block)}");
        distinct.Add($"meshId=0x{meshId:X4} flag=0x{val[0]:X2} block={Hex(block)}");
    }
    else
    {
        Log(conn, $"NOTIFY h=0x{handle:X4} flag=0x{(val.Length > 0 ? val[0] : 0):X2} raw={Hex(val)} (no key)");
    }
}

void ParseEvent(ReadOnlySpan<byte> ev)
{
    // LE Meta (0x3e): Connection Complete (0x01) / Enhanced (0x0a) carry connHandle + peer address.
    if (ev.Length < 2 || ev[0] != 0x3e) return;
    var sub = ev[2];
    if (sub is not (0x01 or 0x0a)) return;
    // status(1) handle(2) role(1) peerAddrType(1) peerAddr(6) ...
    int baseOff = 3; // after subevent code at ev[2]
    if (ev.Length < baseOff + 9) return;
    int handle = ev[baseOff + 1] | (ev[baseOff + 2] << 8);
    var addr = ev.Slice(baseOff + 5, 6);
    var parts = new string[6];
    for (var i = 0; i < 6; i++) parts[i] = addr[5 - i].ToString("x2");
    connMac[handle] = string.Join(":", parts);
}

static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);
void Log(int conn, string msg) => Console.WriteLine($"conn=0x{conn:X3} {msg}");
