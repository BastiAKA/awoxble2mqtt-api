using System.Globalization;

namespace AwoxController.Core.Models;

/// <summary>Which physical transport a light is reachable over.</summary>
public enum LightTransport
{
    Bluetooth,
    Zigbee
}

/// <summary>An 8-bit-per-channel RGB color.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static RgbColor FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6)
            throw new FormatException($"Expected 6-digit hex color, got '{hex}'.");

        return new RgbColor(
            byte.Parse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}

/// <summary>Last known state of a single light. Brightness is normalised to 0-100 %.</summary>
public sealed class LightState
{
    public bool IsOn { get; set; }
    public int BrightnessPercent { get; set; }
    public int? ColorTempMireds { get; set; }
    public RgbColor Color { get; set; }

    /// <summary>
    /// True when the lamp is currently displaying colour (RGB), false when it's in white mode. AwoX
    /// lamps have two independent channels; a brightness command must target the active one or the lamp
    /// jumps modes (e.g. a Connect-C in colour mode flips to white on a white-brightness command). Set
    /// from the live advert (<c>AwoxAdvertStatus.IsColorMode</c>) on Linux.
    /// </summary>
    public bool IsColorMode { get; set; }

    public DateTime LastUpdatedUtc { get; set; }
}

/// <summary>A discovered light device, transport-agnostic.</summary>
public sealed class LightDevice
{
    public string Id { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string Model { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public LightTransport Transport { get; set; }

    public LightState? DesiredState { get; set; }
}
