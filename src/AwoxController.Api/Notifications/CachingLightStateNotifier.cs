using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;

namespace AwoxController.Api.Notifications;

/// <summary>
/// Routes each observed state change into the in-memory <see cref="ILightStateCache"/> (keyed by the
/// lamp MAC the backends pass as the device id). No DB, no I/O — just a dictionary write — so the
/// advert stream the registry reads back never touches the SD card.
/// </summary>
public sealed class CachingLightStateNotifier : ILightStateNotifier
{
    private readonly ILightStateCache _cache;

    public CachingLightStateNotifier(ILightStateCache cache) => _cache = cache;

    public void NotifyStateChanged(string deviceId, LightState state) => _cache.Set(deviceId, state);
}
