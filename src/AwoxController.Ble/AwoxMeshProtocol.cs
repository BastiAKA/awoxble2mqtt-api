using System.Security.Cryptography;
using System.Text;

namespace AwoxController.Ble;

/// <summary>
/// Byte-exact C# port of the AwoX / Telink mesh BLE crypto used by AwoX SmartLight bulbs.
/// Ported from the algorithm in github.com/Leiaz/python-awox-mesh-light (GPLv3) — only the
/// protocol, not the source. The cipher is AES-128-ECB applied to byte-reversed input and
/// output; payloads use a manual CTR-style stream and a CBC-MAC-style 2-byte checksum.
///
/// All methods are static and allocation-light so they can be unit-tested against vectors
/// generated from the reference implementation, independently of any BLE transport.
/// </summary>
public static class AwoxMeshProtocol
{
    /// <summary>The pairing characteristic: login handshake and mesh re-keying.</summary>
    public const string PairCharUuid = "00010203-0405-0607-0809-0a0b0c0d1914";

    /// <summary>The command characteristic: encrypted control packets are written here.</summary>
    public const string CommandCharUuid = "00010203-0405-0607-0809-0a0b0c0d1912";

    /// <summary>The status/notify characteristic: encrypted state notifications arrive here.</summary>
    public const string StatusCharUuid = "00010203-0405-0607-0809-0a0b0c0d1911";

    /// <summary>The OTA characteristic (firmware updates — not used by this controller).</summary>
    public const string OtaCharUuid = "00010203-0405-0607-0809-0a0b0c0d1913";

    // ---- Command opcodes (see python-awox-mesh-light) -------------------------------------

    public const byte CmdPower = 0xD0;            // data: 1 byte 0/1
    public const byte CmdColor = 0xE2;            // data: 0x04, r, g, b
    public const byte CmdColorBrightness = 0xF2;  // data: 1 byte 0x0A..0x64
    public const byte CmdWhiteBrightness = 0xF1;  // data: 1 byte 0x01..0x7F
    public const byte CmdWhiteTemperature = 0xF0; // data: 1 byte 0x00..0x7F
    public const byte CmdPreset = 0xC8;           // data: 1 byte 0..6
    public const byte CmdMeshAddress = 0xE0;      // data: u16 LE
    public const byte CmdMeshReset = 0xE3;

    // ---- Command payload builders (single source of truth for the value ranges) -----------

    /// <summary>Power on/off payload for <see cref="CmdPower"/>.</summary>
    public static byte[] PowerPayload(bool on) => [(byte)(on ? 0x01 : 0x00)];

    /// <summary>Colour payload for <see cref="CmdColor"/>: 0x04, r, g, b.</summary>
    public static byte[] ColorPayload(byte r, byte g, byte b) => [0x04, r, g, b];

    /// <summary>White brightness payload (0-100 % → 0x01..0x7F) for <see cref="CmdWhiteBrightness"/>.</summary>
    public static byte[] WhiteBrightnessPayload(int percent) => [ScaleByte(percent, 0x01, 0x7F)];

    /// <summary>Colour brightness payload (0-100 % → 0x0A..0x64) for <see cref="CmdColorBrightness"/>.</summary>
    public static byte[] ColorBrightnessPayload(int percent) => [ScaleByte(percent, 0x0A, 0x64)];

    /// <summary>
    /// White-temperature payload (mireds 153 cold..500 warm → 0x00 cold..0x7F warm) for
    /// <see cref="CmdWhiteTemperature"/>. Direction CONFIRMED live on Connect C hardware (2026-06-09):
    /// low byte = cold, high byte = warm (same as the Connect-Z white channel).
    /// </summary>
    public static byte[] WhiteTemperaturePayload(int mireds)
    {
        mireds = Math.Clamp(mireds, 153, 500);
        var temp = (int)Math.Round((mireds - 153.0) / (500 - 153) * 0x7F);
        return [(byte)Math.Clamp(temp, 0, 0x7F)];
    }

    private static byte ScaleByte(int percent, int min, int max)
    {
        percent = Math.Clamp(percent, 0, 100);
        return (byte)Math.Clamp((int)Math.Round(percent / 100.0 * max), min, max);
    }

    /// <summary>
    /// The core AwoX primitive: AES-128-ECB over a byte-reversed 16-byte key and value, with the
    /// 16-byte result reversed back. <paramref name="value"/> is right-padded with zeros to 16.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (key.Length != 16)
            throw new ArgumentException("Key must be 16 bytes.", nameof(key));

        Span<byte> k = stackalloc byte[16];
        Span<byte> v = stackalloc byte[16]; // zero-initialised => right-pad with 0x00
        key.CopyTo(k);
        value[..Math.Min(value.Length, 16)].CopyTo(v);
        k.Reverse();
        v.Reverse();

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = k.ToArray();

        var outBuf = new byte[16];
        aes.EncryptEcb(v, outBuf, PaddingMode.None);
        Array.Reverse(outBuf);
        return outBuf;
    }

    /// <summary>
    /// Builds the pairing packet sent to the pair characteristic to start a session.
    /// Layout: 0x0C | session_random(8) | enc(name⊕password, session_random)[0..8].
    /// </summary>
    public static byte[] MakePairPacket(string meshName, string meshPassword, ReadOnlySpan<byte> sessionRandom8)
    {
        var namePass = XorCredentials(meshName, meshPassword);

        // The key is the 8-byte session random right-padded to 16 bytes.
        Span<byte> srKey = stackalloc byte[16];
        sessionRandom8[..8].CopyTo(srKey);
        var enc = Encrypt(srKey, namePass);

        var packet = new byte[1 + 8 + 8];
        packet[0] = 0x0C;
        sessionRandom8[..8].CopyTo(packet.AsSpan(1, 8));
        enc.AsSpan(0, 8).CopyTo(packet.AsSpan(9, 8));
        return packet;
    }

    /// <summary>
    /// Derives the AES session key from the local session random and the device's response random
    /// (bytes 1..9 of the pair reply). key = enc(name⊕password, session_random || response_random).
    /// </summary>
    public static byte[] MakeSessionKey(string meshName, string meshPassword,
        ReadOnlySpan<byte> sessionRandom8, ReadOnlySpan<byte> responseRandom8)
    {
        Span<byte> random = stackalloc byte[16];
        sessionRandom8[..8].CopyTo(random);
        responseRandom8[..8].CopyTo(random[8..]);
        var namePass = XorCredentials(meshName, meshPassword);
        return Encrypt(namePass, random);
    }

    /// <summary>
    /// Builds a 20-byte encrypted command packet for the command characteristic.
    /// Layout: seq(3) | checksum(2) | encrypted_payload(15).
    /// </summary>
    /// <param name="sessionKey">16-byte session key from <see cref="MakeSessionKey"/>.</param>
    /// <param name="mac">Device MAC, e.g. "AA:BB:CC:DD:EE:FF".</param>
    /// <param name="destId">Destination mesh id; 0 addresses the whole mesh.</param>
    /// <param name="command">Command opcode.</param>
    /// <param name="data">Command parameters.</param>
    /// <param name="seq3">3-byte sequence (random in production; fixed for tests).</param>
    public static byte[] MakeCommandPacket(ReadOnlySpan<byte> sessionKey, string mac, ushort destId,
        byte command, ReadOnlySpan<byte> data, ReadOnlySpan<byte> seq3)
    {
        Span<byte> macRev = stackalloc byte[6];
        ParseMac(mac, macRev);
        macRev.Reverse();

        // nonce = macRev[0..4] | 0x01 | seq(3)
        Span<byte> nonce = stackalloc byte[8];
        macRev[..4].CopyTo(nonce);
        nonce[4] = 0x01;
        seq3[..3].CopyTo(nonce[5..]);

        // payload = destId(LE u16) | command | 0x60 0x01 | data, right-padded to 15
        Span<byte> payload = stackalloc byte[15];
        payload[0] = (byte)(destId & 0xFF);
        payload[1] = (byte)(destId >> 8);
        payload[2] = command;
        payload[3] = 0x60;
        payload[4] = 0x01;
        data.CopyTo(payload[5..]);

        var check = MakeChecksum(sessionKey, nonce, payload);
        var enc = CryptPayload(sessionKey, nonce, payload);

        var packet = new byte[20];
        seq3[..3].CopyTo(packet.AsSpan(0, 3));
        check.AsSpan(0, 2).CopyTo(packet.AsSpan(3, 2));
        enc.CopyTo(packet.AsSpan(5));
        return packet;
    }

    // ---- Newer AwoX "Connect-Z" / .ble.zigbee bulbs (firmware 3.x, EGLO-ZM) ----------------
    // Reverse-engineered from an HCI snoop of the official app (see debug/), verified byte-exact
    // against captured ON/OFF/colour/brightness/temperature packets. These bulbs share the SAME
    // login + session key as tlmesh, but the command frame is completely different:
    //   packet(17) = flag(1) || Encrypt(sessionKey, block16)        // flag 0x00 for unicast
    //   block(16)  = [crc8] [len] [dest big-endian(2)] [command...] [zero pad]
    //   crc8 over [len .. command]: poly 0x36, init 0x00, reflected in+out, xorout 0.
    // Command bytes (after dest): ON=01 06 00 01, OFF=01 06 00 00,
    //   COLOUR(hue16 LE)=01 00 03 06 lo hi 02 00, TEMP=01 00 03 0a v 01 02 00,
    //   BRIGHTNESS=01 08 00 04 v 02 00 00 00 00.

    public static byte[] ZigbeePowerCommand(bool on) => [0x01, 0x06, 0x00, (byte)(on ? 1 : 0)];

    /// <summary>Colour by 8-bit hue (0=red, 85=green, 170=blue) + 8-bit saturation (0xFE = full).</summary>
    public static byte[] ZigbeeColorCommand(byte hue, byte sat) => [0x01, 0x00, 0x03, 0x06, hue, sat, 0x02, 0x00];

    /// <summary>Colour from an RGB triple (hue + saturation derived via HSV; brightness is separate).</summary>
    public static byte[] ZigbeeColorCommand(byte r, byte g, byte b)
    {
        var (hue, sat) = RgbToHueSat(r, g, b);
        return ZigbeeColorCommand(hue, sat);
    }

    public static byte[] ZigbeeWhiteTempCommand(byte value) => [0x01, 0x00, 0x03, 0x0A, value, 0x01, 0x02, 0x00];
    public static byte[] ZigbeeBrightnessCommand(byte value) => [0x01, 0x08, 0x00, 0x04, value, 0x02, 0x00, 0x00, 0x00, 0x00];

    /// <summary>RGB → (8-bit hue 0..255 = 0..360°, 8-bit saturation, 0xFE = fully saturated).</summary>
    public static (byte hue, byte sat) RgbToHueSat(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == rd) h = (gd - bd) / delta % 6;
            else if (max == gd) h = (bd - rd) / delta + 2;
            else h = (rd - gd) / delta + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        double s = max <= 0 ? 0 : delta / max;

        var hue = (byte)(Math.Round(h / 360.0 * 255.0) % 256);
        var sat = (byte)Math.Clamp((int)Math.Round(s * 0xFE), 0, 0xFE);
        return (hue, sat);
    }

    /// <summary>CRC-8 used for the Connect-Z command checksum: poly 0x36, init 0, reflected in/out.</summary>
    public static byte Crc8Zigbee(ReadOnlySpan<byte> data)
    {
        static byte Reflect(byte x)
        {
            byte r = 0;
            for (var i = 0; i < 8; i++)
                if ((x & (1 << i)) != 0) r |= (byte)(1 << (7 - i));
            return r;
        }

        byte crc = 0x00;
        foreach (var b in data)
        {
            crc ^= Reflect(b);
            for (var i = 0; i < 8; i++)
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x36 : crc << 1);
        }
        return Reflect(crc);
    }

    /// <summary>
    /// Builds the status-request poll, exactly as the AwoX app sends it (decoded from its btsnoop):
    /// a flag-0x02 packet with dest <c>0x0AFF</c> and payload <c>FF</c> (block <c>[crc][03][0A][FF][FF]</c>).
    /// The app sends this every ~5s; every bulb in the mesh then replies with its full state on the notify
    /// char (1911) as a 0x80 (connected bulb) / 0x82 (relayed) frame. So one poll over a single held
    /// connection reads the whole mesh's state. (The earlier dest 0xffff / payload 0a ff ff was a mis-RE
    /// and only triggered sparse replies.)
    /// </summary>
    public static byte[] MakeZigbeeStatusPoll(ReadOnlySpan<byte> sessionKey)
        => MakeZigbeeCommandPacket(sessionKey, 0x0AFF, [0xFF], flag: 0x02);

    /// <summary>Inverse of <see cref="Encrypt"/>: AES-128-ECB DECRYPT on the reversed key/value.</summary>
    public static byte[] DecryptZigbee(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> enc16)
    {
        Span<byte> k = stackalloc byte[16];
        Span<byte> v = stackalloc byte[16];
        sessionKey[..16].CopyTo(k);
        enc16[..16].CopyTo(v);
        k.Reverse();
        v.Reverse();

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = k.ToArray();

        var outBuf = new byte[16];
        aes.DecryptEcb(v, outBuf, PaddingMode.None);
        Array.Reverse(outBuf);
        return outBuf;
    }

    /// <summary>
    /// Tries to decode a Connect-Z status notification (20-byte frame). The encrypted 16-byte block
    /// sits at an unknown offset inside the frame; we slide over offsets and accept the one whose
    /// decrypted block carries a valid Connect-Z CRC8 ([crc][len][dest..][data]). Returns the offset
    /// and the decrypted 16-byte block, or null if none validates.
    /// </summary>
    public static (int Offset, byte[] Block)? TryDecodeZigbeeStatus(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> frame)
    {
        for (var off = 0; off + 16 <= frame.Length; off++)
        {
            var block = DecryptZigbee(sessionKey, frame.Slice(off, 16));
            int len = block[1];
            if (len >= 2 && len <= 14 && block[0] == Crc8Zigbee(block.AsSpan(1, 1 + len)))
                return (off, block);
        }
        return null;
    }

    /// <summary>
    /// Builds a 17-byte Connect-Z command packet. <paramref name="destId"/> is the bulb's mesh id
    /// (e.g. 0x52CE = 21198), big-endian. <paramref name="command"/> is opcode+data (use the
    /// Zigbee*Command helpers). Encrypted with the same session key as login.
    /// </summary>
    public static byte[] MakeZigbeeCommandPacket(ReadOnlySpan<byte> sessionKey, ushort destId, ReadOnlySpan<byte> command, byte flag = 0x00)
    {
        var len = 2 + command.Length; // dest(2) + command
        Span<byte> block = stackalloc byte[16];
        block[1] = (byte)len;
        block[2] = (byte)(destId >> 8);   // dest big-endian
        block[3] = (byte)(destId & 0xFF);
        command.CopyTo(block[4..]);
        block[0] = Crc8Zigbee(block.Slice(1, 1 + len)); // crc over [len][dest][command]

        var enc = Encrypt(sessionKey, block);
        var packet = new byte[17];
        packet[0] = flag;
        enc.CopyTo(packet.AsSpan(1));
        return packet;
    }

    /// <summary>
    /// Decrypts a 20-byte status/notification packet. Returns the packet with its payload
    /// decrypted (header bytes 0..7 preserved), or null when the 2-byte checksum doesn't match.
    /// </summary>
    public static byte[]? DecryptPacket(ReadOnlySpan<byte> sessionKey, string mac, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 7)
            return null;

        Span<byte> macRev = stackalloc byte[6];
        ParseMac(mac, macRev);
        macRev.Reverse();

        // nonce = macRev[0..3] | packet[0..5]
        Span<byte> nonce = stackalloc byte[8];
        macRev[..3].CopyTo(nonce);
        packet[..5].CopyTo(nonce[3..]);

        var payload = CryptPayload(sessionKey, nonce, packet[7..]);
        var check = MakeChecksum(sessionKey, nonce, payload);
        if (check[0] != packet[5] || check[1] != packet[6])
            return null;

        var result = new byte[7 + payload.Length];
        packet[..7].CopyTo(result);
        payload.CopyTo(result.AsSpan(7));
        return result;
    }

    // ---- Internals ------------------------------------------------------------------------

    /// <summary>CBC-MAC-style checksum: encrypt(nonce|len), then fold each 16-byte payload block in.</summary>
    private static byte[] MakeChecksum(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> payload)
    {
        Span<byte> baseBlock = stackalloc byte[16];
        nonce.CopyTo(baseBlock);
        baseBlock[nonce.Length] = (byte)payload.Length;

        var check = Encrypt(key, baseBlock);
        Span<byte> block = stackalloc byte[16];
        for (var i = 0; i < payload.Length; i += 16)
        {
            block.Clear();
            var n = Math.Min(16, payload.Length - i);
            payload.Slice(i, n).CopyTo(block);
            for (var j = 0; j < 16; j++)
                check[j] ^= block[j];
            check = Encrypt(key, check);
        }

        return check;
    }

    /// <summary>CTR-style stream cipher used for both encryption and decryption (symmetric).</summary>
    private static byte[] CryptPayload(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> payload)
    {
        Span<byte> baseBlock = stackalloc byte[16];
        nonce.CopyTo(baseBlock[1..]); // baseBlock[0] is the counter, starts at 0

        var result = new byte[payload.Length];
        for (var i = 0; i < payload.Length; i += 16)
        {
            var encBase = Encrypt(key, baseBlock);
            var n = Math.Min(16, payload.Length - i);
            for (var j = 0; j < n; j++)
                result[i + j] = (byte)(payload[i + j] ^ encBase[j]);
            baseBlock[0]++;
        }

        return result;
    }

    /// <summary>name (padded to 16) XOR password (padded to 16).</summary>
    private static byte[] XorCredentials(string meshName, string meshPassword)
    {
        Span<byte> n = stackalloc byte[16];
        Span<byte> p = stackalloc byte[16];
        var nb = Encoding.UTF8.GetBytes(meshName);
        var pb = Encoding.UTF8.GetBytes(meshPassword);
        if (nb.Length > 16) throw new ArgumentException("mesh_name can hold max 16 bytes", nameof(meshName));
        if (pb.Length > 16) throw new ArgumentException("mesh_password can hold max 16 bytes", nameof(meshPassword));
        nb.CopyTo(n);
        pb.CopyTo(p);

        var result = new byte[16];
        for (var i = 0; i < 16; i++)
            result[i] = (byte)(n[i] ^ p[i]);
        return result;
    }

    private static void ParseMac(string mac, Span<byte> dest6)
    {
        var clean = mac.Replace(":", "").Replace("-", "");
        if (clean.Length != 12)
            throw new ArgumentException($"Expected a 6-byte MAC, got '{mac}'.", nameof(mac));
        for (var i = 0; i < 6; i++)
            dest6[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
    }
}
