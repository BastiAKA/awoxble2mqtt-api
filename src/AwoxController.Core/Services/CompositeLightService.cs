using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;

namespace AwoxController.Core.Services;

/// <summary>
/// Fans the transport-agnostic <see cref="ILightService"/> calls out to whichever backend
/// owns a given device. Registered as the single <see cref="ILightService"/> the API talks to,
/// so controllers and hubs never need to know whether a light is on Zigbee or Bluetooth.
///
/// Routing is by device id: the first backend that reports a device with that id (or has a
/// cached state for it) wins. Ids are expected to be unique across transports.
/// </summary>
public sealed class CompositeLightService : ILightService
{
    private readonly IReadOnlyList<ILightBackend> _backends;

    public CompositeLightService(IEnumerable<ILightBackend> backends)
        => _backends = backends.ToList();

    // The composite spans transports; report Zigbee as a neutral default. Per-device transport
    // is available on each LightDevice from GetDevices().
    public LightTransport Transport => LightTransport.Zigbee;

    public IReadOnlyCollection<LightDevice> GetDevices()
        => _backends.SelectMany(b => b.GetDevices()).ToList();

    public bool TryGetState(string deviceId, out LightState state)
    {
        foreach (var backend in _backends)
        {
            if (backend.TryGetState(deviceId, out state))
                return true;
        }

        state = default!;
        return false;
    }

    public Task SetPowerAsync(string deviceId, bool on, CancellationToken ct = default)
        => Route(deviceId).SetPowerAsync(deviceId, on, ct);

    public Task ToggleAsync(string deviceId, CancellationToken ct = default)
        => Route(deviceId).ToggleAsync(deviceId, ct);

    public Task SetBrightnessAsync(string deviceId, int percent, CancellationToken ct = default)
        => Route(deviceId).SetBrightnessAsync(deviceId, percent, ct);

    public Task SetColorAsync(string deviceId, RgbColor color, CancellationToken ct = default)
        => Route(deviceId).SetColorAsync(deviceId, color, ct);

    public Task SetColorTemperatureAsync(string deviceId, int mireds, CancellationToken ct = default)
        => Route(deviceId).SetColorTemperatureAsync(deviceId, mireds, ct);

    /// <summary>
    /// Picks the backend that owns <paramref name="deviceId"/>. Falls back to the single backend
    /// when only one is registered, so a device that hasn't been discovered yet is still routable.
    /// </summary>
    private ILightService Route(string deviceId)
    {
        foreach (var backend in _backends)
        {
            if (backend.TryGetState(deviceId, out _) ||
                backend.GetDevices().Any(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)))
            {
                return backend;
            }
        }

        if (_backends.Count == 1)
            return _backends[0];

        throw new KeyNotFoundException($"No light backend owns device '{deviceId}'.");
    }
}
