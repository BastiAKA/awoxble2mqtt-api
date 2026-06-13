using AwoxController.Ble;
using Xunit;

namespace AwoxController.Ble.Tests;

/// <summary>
/// Locks the Connect-Z advertisement decoder against frames captured live from the "Wohnzimmer
/// Esstisch" lamp (mesh 0x52CE) on 2026-06-10 — power/brightness/hue/sat/mode were all confirmed
/// against the app in real time. The bytes are the company-stripped manufacturer value as BlueZ
/// hands it over (company id 0x0160 is the dictionary key).
/// </summary>
public class AwoxAdvertStatusTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void TryParse_ColourOn_DecodesFields()
    {
        var ok = AwoxAdvertStatus.TryParse(0x0160,
            Hex("9620B352DA38030001CE5203EDFFFF65FE043C6000000000000000"), out var s);

        Assert.True(ok);
        Assert.Equal(0x52CE, s.MeshId);
        Assert.True(s.IsOn);
        Assert.True(s.IsColorMode);
        Assert.Equal(0xED, s.Brightness);
        Assert.Equal(93, s.BrightnessPercent); // 237/254 ≈ 93 % (0xFE = full = 100 %)
        Assert.Equal(0x65, s.Hue);
        Assert.Equal(0xFE, s.Sat);
    }

    [Fact]
    public void TryParse_Off_ClearsPowerBitButKeepsColour()
    {
        // Same colour state, mode 0x02 = off (bit0 clear, bit1 colour set).
        var ok = AwoxAdvertStatus.TryParse(0x0160,
            Hex("9620B352DA38030001CE5202FEFFFFBB06043C6000000000000000"), out var s);

        Assert.True(ok);
        Assert.False(s.IsOn);
        Assert.True(s.IsColorMode);
        Assert.Equal(0xBB, s.Hue);
        Assert.Equal(0x06, s.Sat);
    }

    [Fact]
    public void TryParse_WhiteOn_ReadsWhiteTemp()
    {
        // mode 0x01 = on + white; hue/sat are FF FF (unused), white temp in byte 13.
        var ok = AwoxAdvertStatus.TryParse(0x0160,
            Hex("9620B352DA38030001CE5201FEB200FFFF043C6000000000000000"), out var s);

        Assert.True(ok);
        Assert.True(s.IsOn);
        Assert.False(s.IsColorMode);
        Assert.Equal(0xB2, s.WhiteTemp);
    }

    [Fact]
    public void TryParse_BaseAdvert_NoPayload_Rejected()
    {
        // The short 6-byte base advert carries no state.
        Assert.False(AwoxAdvertStatus.TryParse(0x0160, Hex("9620B352DA38"), out _));
    }

    [Fact]
    public void TryParse_WrongCompany_Rejected()
    {
        Assert.False(AwoxAdvertStatus.TryParse(0x004C,
            Hex("9620B352DA38030001CE5203EDFFFF65FE043C6000000000000000"), out _));
    }

    // Connect-C uses a DIFFERENT layout: header 02 02 at [6:7] and a DIRECT RGB triple at [13:15]
    // (no HSV). Bytes captured live from the "Badezimmer" lamp (mesh yplR8A4h) on 2026-06-10, lit BLUE.
    [Fact]
    public void TryParse_ConnectC_DecodesDirectRgb()
    {
        var ok = AwoxAdvertStatus.TryParse(0x0160,
            Hex("571091292038020206912903640335FC0400000000000000000000"), out var s);

        Assert.True(ok);
        Assert.True(s.IsConnectC);
        Assert.True(s.IsOn);          // mode 0x03 bit0
        Assert.True(s.IsColorMode);   // mode 0x03 bit1
        Assert.Equal(100, s.BrightnessPercent); // 0x64 on the Connect-C 0–0x64 scale = full
        var rgb = s.ToRgb();
        Assert.Equal(0x03, rgb.R);
        Assert.Equal(0x35, rgb.G);
        Assert.Equal(0xFC, rgb.B);    // dominant blue
    }

    // A Connect-Z frame (header 03 00) must still take the HSV path, not the Connect-C one.
    [Fact]
    public void TryParse_ConnectZ_NotFlaggedAsConnectC()
    {
        Assert.True(AwoxAdvertStatus.TryParse(0x0160,
            Hex("9620B352DA38030001CE5203EDFFFF65FE043C6000000000000000"), out var s));
        Assert.False(s.IsConnectC);
    }
}
