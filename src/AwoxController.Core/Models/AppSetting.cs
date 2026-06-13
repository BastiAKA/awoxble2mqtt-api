namespace AwoxController.Core.Models;

/// <summary>
/// A single runtime-tunable configuration value, persisted so it can be changed without a redeploy
/// (e.g. the BLE poll interval). The value is always stored as text; the consuming code parses it to
/// the type it expects for the given <see cref="Key"/> (see <c>AppSettingKeys</c>). This keeps the
/// table to one value column — the standard config approach — instead of typed columns per row.
/// </summary>
public sealed class AppSetting
{
    /// <summary>Stable, dotted setting key, e.g. "ble.poll_interval_seconds". Primary key.</summary>
    public string Key { get; set; } = "";

    /// <summary>The value, as text. Parsed to int/double/bool/string by the consumer per key.</summary>
    public string Value { get; set; } = "";

    /// <summary>Optional human-readable note shown in the settings UI.</summary>
    public string? Description { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
