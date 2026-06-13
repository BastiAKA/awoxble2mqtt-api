using System.Text;
using AwoxController.Ble;
using Xunit;

namespace AwoxController.Ble.Tests;

/// <summary>
/// Verifies the C# crypto port byte-for-byte against vectors generated from the reference
/// implementation (github.com/Leiaz/python-awox-mesh-light) with pycryptodome. If any of these
/// fail, the bulbs will reject our packets — the byte order is unforgiving.
/// </summary>
public class AwoxMeshProtocolTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static readonly byte[] Key0To15 =
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // Session key derived from name="mymesh", password="secret123", sr=AA*8, rr=55*8.
    private static readonly byte[] SessionKey = Hex("e18a511b9939443df3c0b7cd3df2f52e");

    [Fact]
    public void Encrypt_ShortValue_MatchesReference()
    {
        var result = AwoxMeshProtocol.Encrypt(Key0To15, [0x01, 0x02, 0x03, 0x04]);
        Assert.Equal("2e43a8b89613109b70953fe87f8b7b77", Hex(result));
    }

    [Fact]
    public void Encrypt_FullBlock_MatchesReference()
    {
        var value = new byte[] { 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
        var result = AwoxMeshProtocol.Encrypt(Key0To15, value);
        Assert.Equal("e28586ec4f61465d5bc0afeb819372e9", Hex(result));
    }

    [Fact]
    public void MakePairPacket_MatchesReference()
    {
        var sr = Enumerable.Repeat((byte)0xAA, 8).ToArray();
        var packet = AwoxMeshProtocol.MakePairPacket("mymesh", "secret123", sr);
        Assert.Equal("0caaaaaaaaaaaaaaaa5dac57355a7c1203", Hex(packet));
    }

    [Fact]
    public void MakeSessionKey_MatchesReference()
    {
        var sr = Enumerable.Repeat((byte)0xAA, 8).ToArray();
        var rr = Enumerable.Repeat((byte)0x55, 8).ToArray();
        var key = AwoxMeshProtocol.MakeSessionKey("mymesh", "secret123", sr, rr);
        Assert.Equal("e18a511b9939443df3c0b7cd3df2f52e", Hex(key));
    }

    [Fact]
    public void MakeCommandPacket_PowerOn_MatchesReference()
    {
        var packet = AwoxMeshProtocol.MakeCommandPacket(
            SessionKey, "AA:BB:CC:DD:EE:FF", destId: 0,
            AwoxMeshProtocol.CmdPower, [0x01], seq3: [0x11, 0x22, 0x33]);
        Assert.Equal("112233cde67e85f9def54e2a93c92209e80ddfd2", Hex(packet));
    }

    [Fact]
    public void MakeCommandPacket_Color_MatchesReference()
    {
        var packet = AwoxMeshProtocol.MakeCommandPacket(
            SessionKey, "AA:BB:CC:DD:EE:FF", destId: 0,
            AwoxMeshProtocol.CmdColor, [0x04, 255, 128, 0], seq3: [0x11, 0x22, 0x33]);
        Assert.Equal("112233d5d17e85cbdef54bd513c92209e80ddfd2", Hex(packet));
    }

    // An inbound status packet forged with the reference implementation (uses the device's
    // nonce scheme: macRev[0..3] | packet[0..5]). Decrypting it must recover the plaintext.
    private const string InboundStatusPacket = "0102030405340999594367dd1005c68b6bd9a0df";
    private const string InboundStatusDecrypted = "010203040534090000d060010100000000000000";

    [Fact]
    public void DecryptPacket_ValidStatusPacket_MatchesReference()
    {
        var decrypted = AwoxMeshProtocol.DecryptPacket(SessionKey, "AA:BB:CC:DD:EE:FF", Hex(InboundStatusPacket));

        Assert.NotNull(decrypted);
        Assert.Equal(InboundStatusDecrypted, Hex(decrypted!));
    }

    [Fact]
    public void DecryptPacket_WrongKey_ReturnsNull()
    {
        var wrongKey = (byte[])SessionKey.Clone();
        wrongKey[0] ^= 0xFF;

        Assert.Null(AwoxMeshProtocol.DecryptPacket(wrongKey, "AA:BB:CC:DD:EE:FF", Hex(InboundStatusPacket)));
    }
}
