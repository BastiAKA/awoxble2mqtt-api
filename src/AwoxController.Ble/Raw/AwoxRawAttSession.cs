using System.Diagnostics;
using System.Security.Cryptography;

namespace AwoxController.Ble.Raw;

/// <summary>
/// Drives one AwoX mesh session over a raw ATT channel (<see cref="RawAttSocket"/>): performs the
/// AwoX login, then polls + receives the bulb's unsolicited status pushes WITHOUT ever writing the
/// notify CCCD. This is the Linux path to real Connect-Z status — see <see cref="RawAttSocket"/> for
/// why the CCCD must be avoided. The crypto is the shared <see cref="AwoxMeshProtocol"/>.
///
/// ATT handles are the AwoX mesh service layout (service 0x0010–0x001f), confirmed from a btmon
/// capture: pair = 0x001e, command = 0x0015, status = 0x0012 (CCCD 0x0013, never written).
/// </summary>
public static class AwoxRawAttSession
{
    private const ushort PairHandle = 0x001e;
    private const ushort CommandHandle = 0x0015;
    private const ushort StatusHandle = 0x0012;

    private const byte OpWriteRequest = 0x12;
    private const byte OpWriteResponse = 0x13;
    private const byte OpReadRequest = 0x0a;
    private const byte OpReadResponse = 0x0b;
    private const byte OpWriteCommand = 0x52;
    private const byte OpErrorResponse = 0x01;
    private const byte OpHandleValueNotification = 0x1b;

    /// <summary>Connects + logs in; returns the open socket and derived session key.</summary>
    private static (RawAttSocket sock, byte[] key) Login(string mac, string meshName, string meshPassword, Action<string> log)
    {
        var sock = new RawAttSocket();
        log($"Connecting raw ATT to {mac} (no BlueZ GATT)...");
        sock.Connect(mac, recvTimeoutMs: 1500);
        log("L2CAP ATT channel open.");

        var sessionRandom = RandomNumberGenerator.GetBytes(8);
        var pair = AwoxMeshProtocol.MakePairPacket(meshName, meshPassword, sessionRandom);
        SendAndExpect(sock, BuildPdu(OpWriteRequest, PairHandle, pair), OpWriteResponse, log, "pair write");

        var readReply = SendAndRead(sock, BuildPdu(OpReadRequest, PairHandle), log);
        if (readReply.Length < 9 || readReply[0] != OpReadResponse)
            throw new InvalidOperationException($"Unexpected pair read reply: {Convert.ToHexString(readReply)}");

        var reply = readReply.AsSpan(1).ToArray();
        if (reply[0] != 0x0D)
            throw new InvalidOperationException(reply[0] == 0x0E
                ? "Login rejected (0x0E): wrong MeshName/MeshPassword."
                : $"Login failed, unexpected reply 0x{reply[0]:X2}.");

        var key = AwoxMeshProtocol.MakeSessionKey(meshName, meshPassword, sessionRandom, reply.AsSpan(1, 8));
        log($"Logged in. Session key: {Convert.ToHexString(key)}");

        // Replicate the app's status-enable: after login the app writes 0x01 to the status char
        // (handle 0x0012) — NOT a CCCD (the mesh uses no CCCD). This is the Telink/AwoX "start
        // reporting" trigger; without it the bulb may report less.
        try { SendAndExpect(sock, BuildPdu(OpWriteRequest, StatusHandle, new byte[] { 0x01 }), OpWriteResponse, log, "status-enable"); }
        catch (Exception ex) { log($"status-enable write failed (continuing): {ex.Message}"); }

        return (sock, key);
    }

    /// <summary>
    /// Connects, logs in, then polls every ~3s and decodes every status push for <paramref name="seconds"/>.
    /// </summary>
    public static int Probe(string mac, string meshName, string meshPassword, int seconds, Action<string> log)
    {
        var (sock, key) = Login(mac, meshName, meshPassword, log);
        using var _ = sock;
        log("NO CCCD written — listening for unsolicited pushes...");

        var buf = new byte[64];
        var frames = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            sock.Send(BuildPdu(OpWriteCommand, CommandHandle, AwoxMeshProtocol.MakeZigbeeStatusPoll(key)));
            var drainUntil = sw.Elapsed + TimeSpan.FromSeconds(3);
            while (sw.Elapsed < drainUntil)
            {
                var (ok, value) = ReadNotification(sock, buf);
                if (!ok) continue;
                frames++;
                LogBlock(key, value!, log);
            }
        }
        log($"Done. {frames} status push(es) received in {seconds}s.");
        return frames;
    }

    /// <summary>
    /// Controlled colour/brightness mapping: sends KNOWN hue/sat/brightness commands to <paramref name="destId"/>
    /// and reads back that bulb's resulting status block, so we can map status bytes [12..15] to the real
    /// hue/sat/brightness. Sweeps hue, then saturation, then brightness.
    /// </summary>
    public static void ColorMap(string mac, string meshName, string meshPassword, ushort destId, Action<string> log)
    {
        var (sock, key) = Login(mac, meshName, meshPassword, log);
        using var _ = sock;

        void Apply(string label, byte[] command)
        {
            sock.Send(BuildPdu(OpWriteCommand, CommandHandle, AwoxMeshProtocol.MakeZigbeeCommandPacket(key, destId, command)));
            // The bulb pushes its new state shortly after the command; take the LAST matching frame in
            // the window (earlier frames in the socket buffer are stale from before this command).
            var block = ReadLastBlockFor(sock, key, destId, 2500);
            var tail = block is null ? "(no status)" : $"[12..15]={block[12]:X2} {block[13]:X2} {block[14]:X2} {block[15]:X2}  full={Convert.ToHexString(block)}";
            log($"{label,-22} -> {tail}");
        }

        log("--- HUE sweep (sat=0xFE full) ---");
        foreach (var hue in new byte[] { 0x00, 0x15, 0x2A, 0x40, 0x55, 0x6A, 0x80, 0x95, 0xAA, 0xC0, 0xD5, 0xEA })
            Apply($"hue=0x{hue:X2} sat=FE", AwoxMeshProtocol.ZigbeeColorCommand(hue, 0xFE));

        log("--- SAT sweep (hue=0x00 red) ---");
        foreach (var sat in new byte[] { 0xFE, 0xC0, 0x80, 0x40, 0x10 })
            Apply($"hue=00 sat=0x{sat:X2}", AwoxMeshProtocol.ZigbeeColorCommand(0x00, sat));

        log("--- BRIGHTNESS sweep ---");
        foreach (var v in new byte[] { 0xFE, 0xC0, 0x80, 0x40, 0x10 })
            Apply($"brightness=0x{v:X2}", AwoxMeshProtocol.ZigbeeBrightnessCommand(v));

        log("ColorMap done.");
    }

    /// <summary>
    /// Queries a bulb's extended attributes (RGB colour etc.) the way the app does: a flag-0x02
    /// command with dest = (class&lt;&lt;8)|meshId_low, cmd = [meshId_high, subIndex]. The bulb replies
    /// with a flag-0x35 / 0x3C notification carrying the attribute. Prints each response so the bytes
    /// can be mapped to the set colour. Set the bulb to a KNOWN colour first, then run this.
    /// </summary>
    public static void ColorQuery(string mac, string meshName, string meshPassword, ushort meshId, Action<string> log)
    {
        var (sock, key) = Login(mac, meshName, meshPassword, log);
        using var _ = sock;

        void Query(byte cls, byte sub)
        {
            var dest = (ushort)((cls << 8) | (meshId & 0xFF));
            var cmd = new byte[] { (byte)(meshId >> 8), sub };
            sock.Send(BuildPdu(OpWriteCommand, CommandHandle, AwoxMeshProtocol.MakeZigbeeCommandPacket(key, dest, cmd, flag: 0x02)));
            // Collect ALL frames for this meshId in the window — one query can yield several frames
            // (e.g. a static descriptor AND the colour frame); don't stop at the first.
            foreach (var b in ReadAllBlocks(sock, key, meshId, 900))
                log($"class 0x{cls:X2} sub 0x{sub:X2} -> {Convert.ToHexString(b)}");
        }

        Query(0x3C, 0x01); // the multi-frame colour query (mirrors the app's 0x3C27 B601)
        foreach (var s in new byte[] { 0x02, 0x03, 0x05, 0x07, 0x08 }) Query(0x35, (byte)s);
        log("ColorQuery done.");
    }

    /// <summary>
    /// Reads EVERY characteristic value handle in/around the mesh service (0x0011–0x0030) via plain ATT
    /// Read Requests, printing raw + AES-decrypted bytes. The 2nd phone shows the RGB colour before it
    /// finishes logging in — so the colour must live in a readable characteristic. Run at a known colour.
    /// </summary>
    public static void ReadAll(string mac, string meshName, string meshPassword, Action<string> log)
    {
        var (sock, key) = Login(mac, meshName, meshPassword, log);
        using var _ = sock;
        var buf = new byte[64];
        for (ushort h = 0x0011; h <= 0x0030; h++)
        {
            sock.Send(BuildPdu(OpReadRequest, h));
            var res = "(no resp)";
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 800)
            {
                var n = sock.Receive(buf);
                if (n == 0) continue;
                if (buf[0] == OpReadResponse)
                {
                    var v = buf[1..n];
                    var dec = v.Length >= 16 ? "  dec=" + Convert.ToHexString(AwoxMeshProtocol.DecryptZigbee(key, v.AsSpan(0, 16))) : "";
                    res = $"raw={Convert.ToHexString(v)}{dec}";
                    break;
                }
                if (buf[0] == OpErrorResponse) { res = $"ATT-error 0x{(n >= 5 ? buf[4] : 0):X2}"; break; }
            }
            log($"handle 0x{h:04X}: {res}");
        }
        log("ReadAll done.");
    }

    /// <summary>Collects ALL distinct decrypted blocks for the given meshId within the window.</summary>
    private static List<byte[]> ReadAllBlocks(RawAttSocket sock, byte[] key, ushort meshId, int ms)
    {
        var buf = new byte[64];
        var sw = Stopwatch.StartNew();
        var seen = new HashSet<string>();
        var blocks = new List<byte[]>();
        while (sw.ElapsedMilliseconds < ms)
        {
            var (ok, value) = ReadNotification(sock, buf);
            if (!ok || value!.Length < 17) continue;
            var block = AwoxMeshProtocol.DecryptZigbee(key, value.AsSpan(1, 16));
            if ((ushort)(block[0] | (block[1] << 8)) != meshId) continue;
            if (seen.Add(Convert.ToHexString(block))) blocks.Add(block);
        }
        return blocks;
    }

    /// <summary>
    /// Collects status frames for <paramref name="ms"/> (polling at start + midpoint) and returns the
    /// LAST one whose decrypted block is the given meshId — i.e. the freshest state after a command,
    /// past any stale frames buffered from before.
    /// </summary>
    private static byte[]? ReadLastBlockFor(RawAttSocket sock, byte[] key, ushort meshId, int ms)
    {
        var buf = new byte[64];
        var sw = Stopwatch.StartNew();
        var poll = BuildPdu(OpWriteCommand, CommandHandle, AwoxMeshProtocol.MakeZigbeeStatusPoll(key));
        sock.Send(poll);
        var repolled = false;
        byte[]? last = null;
        while (sw.ElapsedMilliseconds < ms)
        {
            if (!repolled && sw.ElapsedMilliseconds > ms / 2) { sock.Send(poll); repolled = true; }
            var (ok, value) = ReadNotification(sock, buf);
            if (!ok || value!.Length < 17) continue;
            var block = AwoxMeshProtocol.DecryptZigbee(key, value.AsSpan(1, 16));
            var id = (ushort)(block[0] | (block[1] << 8));
            if (id == meshId && block.Length >= 16) last = block;
        }
        return last;
    }

    private static (bool ok, byte[]? value) ReadNotification(RawAttSocket sock, byte[] buf)
    {
        var n = sock.Receive(buf);
        if (n < 3 || buf[0] != OpHandleValueNotification) return (false, null);
        var handle = (ushort)(buf[1] | (buf[2] << 8));
        if (handle != StatusHandle) return (false, null);
        return (true, buf[3..n]);
    }

    private static void LogBlock(byte[] key, byte[] value, Action<string> log)
    {
        log($"PUSH flag=0x{value[0]:X2} raw={Convert.ToHexString(value)}");
        if (value.Length >= 17)
            log($"   block = {Convert.ToHexString(AwoxMeshProtocol.DecryptZigbee(key, value.AsSpan(1, 16)))}");
    }

    private static byte[] BuildPdu(byte opcode, ushort handle, ReadOnlySpan<byte> value = default)
    {
        var pdu = new byte[3 + value.Length];
        pdu[0] = opcode;
        pdu[1] = (byte)(handle & 0xff);
        pdu[2] = (byte)(handle >> 8);
        value.CopyTo(pdu.AsSpan(3));
        return pdu;
    }

    private static void SendAndExpect(RawAttSocket sock, byte[] pdu, byte expectedOpcode, Action<string> log, string what)
    {
        sock.Send(pdu);
        var buf = new byte[64];
        for (var i = 0; i < 5; i++)
        {
            var n = sock.Receive(buf);
            if (n == 0) continue;
            if (buf[0] == expectedOpcode) return;
            if (buf[0] == OpErrorResponse)
                throw new InvalidOperationException($"{what} got ATT error: {Convert.ToHexString(buf.AsSpan(0, n))}");
        }
        throw new InvalidOperationException($"{what}: no 0x{expectedOpcode:X2} response.");
    }

    private static byte[] SendAndRead(RawAttSocket sock, byte[] pdu, Action<string> log)
    {
        sock.Send(pdu);
        var buf = new byte[64];
        for (var i = 0; i < 5; i++)
        {
            var n = sock.Receive(buf);
            if (n == 0) continue;
            if (buf[0] == OpReadResponse) return buf.AsSpan(0, n).ToArray();
        }
        throw new InvalidOperationException("No read response.");
    }
}
