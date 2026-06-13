using AwoxController.Core.Models;

namespace AwoxController.Ble;

/// <summary>
/// Builds the tolerant "did the lamp actually reach the commanded state?" predicates used to verify a
/// relayed command against the target's live advert (<see cref="BleCommand.ExpectedState"/>). Tolerant
/// on purpose: lamps clamp/round values and report a separate brightness per mode, and a false-negative
/// only costs one needless direct reconnect — never a wrong state. Match the LAMP, not our exact command.
/// </summary>
public static class ExpectedStatePredicates
{
    private const int BrightnessTolerancePct = 6;
    private const int MiredsTolerance = 40;
    private const double HueToleranceDeg = 30;

    /// <summary>Power is exact — the most common and most reliable confirmation.</summary>
    public static Func<LightState, bool> Power(bool on) => s => s.IsOn == on;

    /// <summary>Brightness can land on either channel, so just check the level (and that it's on).</summary>
    public static Func<LightState, bool> Brightness(int percent)
        => s => s.IsOn && Math.Abs(s.BrightnessPercent - percent) <= BrightnessTolerancePct;

    /// <summary>A colour command must put the lamp in colour mode; hue should roughly match (the value/
    /// brightness component varies and is carried separately).</summary>
    public static Func<LightState, bool> Color(RgbColor target)
    {
        var targetHue = Hue(target);
        return s => s.IsColorMode && HueClose(Hue(s.Color), targetHue);
    }

    /// <summary>A colour-temperature command must put the lamp in white mode at ~the requested mireds.</summary>
    public static Func<LightState, bool> ColorTemp(int mireds)
        => s => !s.IsColorMode
             && (s.ColorTempMireds is not int m || Math.Abs(m - mireds) <= MiredsTolerance);

    private static bool HueClose(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        if (d > 180) d = 360 - d;
        return d <= HueToleranceDeg;
    }

    // RGB → hue in degrees [0,360). Greys (delta ~0) return 0; the colour predicate also gates on IsColorMode.
    private static double Hue(RgbColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta < 1e-6) return 0;

        double h;
        if (max == r) h = ((g - b) / delta) % 6;
        else if (max == g) h = (b - r) / delta + 2;
        else h = (r - g) / delta + 4;

        h *= 60;
        return h < 0 ? h + 360 : h;
    }
}
