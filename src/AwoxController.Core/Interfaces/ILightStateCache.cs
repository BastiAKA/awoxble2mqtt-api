using AwoxController.Core.Models;

namespace AwoxController.Core.Interfaces;

/// <summary>
/// In-memory cache of each lamp's last observed live state, keyed by MAC. Fed by the light backends
/// (advert scan + control path) via the state notifier and read back by the device registry so REST
/// reflects real current state — WITHOUT writing to the DB. The registry lives on an SD card, so the
/// high-frequency advert stream must never persist; this cache absorbs it instead.
/// </summary>
public interface ILightStateCache
{
    /// <summary>Records the latest state for a lamp (MAC in any separator/case form).</summary>
    void Set(string mac, LightState state);

    /// <summary>Gets the cached state for a lamp MAC, if one has been observed this run.</summary>
    bool TryGet(string mac, out LightState state);
}
