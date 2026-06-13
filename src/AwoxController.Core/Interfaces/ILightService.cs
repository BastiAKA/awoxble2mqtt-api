using AwoxController.Core.Models;

namespace AwoxController.Core.Interfaces;

/// <summary>
/// Transport-agnostic contract for controlling lights. The Zigbee backend (via
/// Zigbee2MQTT) and the future Bluetooth backend (via BlueZ) both implement this,
/// so controllers and SignalR hubs never need to know which transport is in play.
/// </summary>
public interface ILightService
{
    LightTransport Transport { get; }

    /// <summary>Currently known devices (populated from Zigbee2MQTT's retained device list).</summary>
    IReadOnlyCollection<LightDevice> GetDevices();

    /// <summary>Last cached state for a device, if any state message has been received.</summary>
    bool TryGetState(string deviceId, out LightState state);

    Task SetPowerAsync(string deviceId, bool on, CancellationToken ct = default);
    Task ToggleAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Set brightness as a 0-100 % value. Backends convert to their native range.</summary>
    Task SetBrightnessAsync(string deviceId, int percent, CancellationToken ct = default);

    Task SetColorAsync(string deviceId, RgbColor color, CancellationToken ct = default);

    /// <summary>Color temperature in mireds (153 = cold/6500K … 500 = warm/2000K).</summary>
    Task SetColorTemperatureAsync(string deviceId, int mireds, CancellationToken ct = default);
}
