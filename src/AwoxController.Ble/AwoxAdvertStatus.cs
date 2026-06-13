using AwoxController.Core.Models;

namespace AwoxController.Ble;

/// <summary>
/// Live state decoded from an AwoX "Connect-Z" lamp's BLE <b>advertisement</b> (manufacturer-specific
/// data, company id <c>0x0160</c>). The lamp broadcasts its full state UNENCRYPTED, so reading it needs
/// NO connection, login or session key — a passive scan is enough. This is the only way to read status
/// without stealing the link from the AwoX app / hub. Verified live against the hardware (2026-06-10):
/// power, brightness, hue/saturation and white colour-temperature all tracked the app in real time.
///
/// BlueZ delivers manufacturer data with the 2-byte company id stripped (it is the dictionary key), so
/// the offsets below are into that company-stripped value:
/// <code>
///   [0:1]=type 9620   [2:5]=lower MAC bytes (LE)   [6:8]=const 03 00 01
///   [9:10]=meshId (LE) [11]=mode/power  [12]=brightness
///   [13:14]=white colour-temp (white mode)  [15:16]=hue,sat (colour mode)  [17:19]=const 04 3C 60
/// </code>
/// Byte 11 is a bitfield: <c>bit0 (0x01) = power on</c>, <c>bit1 (0x02) = colour mode</c> (clear = white).
/// When OFF the long advert is still broadcast with brightness/colour retained — only bit0 drops.
/// The short 6-byte "base" advert (<c>9620…</c>) carries no state and is rejected here.
/// </summary>
public readonly record struct AwoxAdvertStatus(
    ushort MeshId, bool IsOn, bool IsColorMode, byte Brightness, byte WhiteTemp, byte Hue, byte Sat,
    bool IsConnectC = false, RgbColor DirectColor = default)
{
    /// <summary>Bluetooth SIG company id AwoX uses in its manufacturer data (little-endian <c>60 01</c>).</summary>
    public const ushort CompanyId = 0x0160;

    /// <summary>
    /// Brightness as 0–100 %. Connect-Z uses a 0x00–0xFE raw range; Connect-C the classic AwoX scales —
    /// colour max 0x64, white max 0x7F (matches the command path's Color/WhiteBrightnessMax).
    /// </summary>
    public int BrightnessPercent => IsConnectC
        ? (int)Math.Round(Brightness / (IsColorMode ? 100.0 : 127.0) * 100.0)
        : (int)Math.Round(Brightness / 254.0 * 100.0);

    /// <summary>
    /// Tries to decode an AwoX status advertisement. <paramref name="companyId"/> must be
    /// <see cref="CompanyId"/> and <paramref name="data"/> the company-stripped manufacturer value (as
    /// BlueZ hands it over). Dispatches on the layout header at <c>[6:7]</c>: <c>03 00</c> = Connect-Z
    /// (HSV colour), <c>02 02</c> = Connect-C (direct RGB). Returns false for the short "base" advert or
    /// any unknown layout.
    /// </summary>
    public static bool TryParse(ushort companyId, ReadOnlySpan<byte> data, out AwoxAdvertStatus status)
    {
        status = default;
        if (companyId != CompanyId || data.Length < 17)
            return false;

        // Connect-Z: `9620 <mac LE×4> 03 00 01 <meshId LE> mode bri whiteTemp×2 hue sat 04 3C 60`
        if (data[6] == 0x03 && data[7] == 0x00)
        {
            var mode = data[11];
            status = new AwoxAdvertStatus(
                MeshId: (ushort)(data[9] | (data[10] << 8)),
                IsOn: (mode & 0x01) != 0,
                IsColorMode: (mode & 0x02) != 0,
                Brightness: data[12],
                WhiteTemp: data[13],
                Hue: data[15],
                Sat: data[16]);
            return true;
        }

        // Connect-C: `<type×2> <mac LE×4> 02 02 06 <meshId LE> mode bri ...`. Colour mode carries a
        // DIRECT RGB triple at [13:15]; white mode carries the colour-temp at [13] (with [14:15]=00).
        if (data[6] == 0x02 && data[7] == 0x02)
        {
            var mode = data[11];
            var colour = (mode & 0x02) != 0;
            status = new AwoxAdvertStatus(
                MeshId: (ushort)(data[9] | (data[10] << 8)),
                IsOn: (mode & 0x01) != 0,
                IsColorMode: colour,
                Brightness: data[12],
                WhiteTemp: colour ? (byte)0 : data[13],
                Hue: 0,
                Sat: 0,
                IsConnectC: true,
                DirectColor: colour ? new RgbColor(data[13], data[14], data[15]) : default);
            return true;
        }

        return false; // unknown layout / base advert
    }

    /// <summary>
    /// The advertised colour as an RGB triple at full value (brightness is a separate field).
    /// Connect-Z reports HSV (converted here); Connect-C reports RGB directly.
    /// </summary>
    public RgbColor ToRgb() => IsConnectC ? DirectColor : HsvToRgb(Hue, Sat);

    // Hue/sat are 0–255; value is fixed at full (brightness is carried separately by the lamp).
    private static RgbColor HsvToRgb(byte hue, byte sat)
    {
        double h = hue / 255.0 * 6.0;
        double s = sat / 255.0;
        const double v = 1.0;

        int i = (int)Math.Floor(h) % 6;
        if (i < 0) i += 6;
        double f = h - Math.Floor(h);
        double p = v * (1 - s);
        double q = v * (1 - s * f);
        double t = v * (1 - s * (1 - f));

        var (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return new RgbColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }
}
