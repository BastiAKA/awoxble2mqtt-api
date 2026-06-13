using AwoxController.Core.Models;

namespace AwoxController.Core.Interfaces;

/// <summary>
/// Publishes live light-state changes to connected clients (implemented with SignalR in the API
/// layer). Light backends call this whenever a device's state ACTUALLY changes, so the frontend
/// updates in real time without polling.
///
/// In-memory fan-out only — it never persists anything, deliberately keeping the high-frequency status
/// stream off the SD card (only user-initiated commands persist <c>LastState</c>). Backends inject it
/// as an optional dependency, so they keep working when no push transport is wired (the call no-ops).
/// </summary>
public interface ILightStateNotifier
{
    /// <summary>Broadcasts that <paramref name="deviceId"/> now has the given <paramref name="state"/>.</summary>
    void NotifyStateChanged(string deviceId, LightState state);
}
