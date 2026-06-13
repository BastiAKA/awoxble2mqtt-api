using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;

namespace AwoxController.Api.Notifications;

/// <summary>
/// Fans a state change out to several <see cref="ILightStateNotifier"/> sinks — the SignalR broadcast
/// (live UI push) and the DB persister (so REST reflects it). Null sinks are ignored so the persister
/// can be left out when no device registry is configured.
/// </summary>
public sealed class CompositeLightStateNotifier : ILightStateNotifier
{
    private readonly IReadOnlyList<ILightStateNotifier> _sinks;

    public CompositeLightStateNotifier(params ILightStateNotifier?[] sinks)
        => _sinks = sinks.Where(s => s is not null).Cast<ILightStateNotifier>().ToList();

    public void NotifyStateChanged(string deviceId, LightState state)
    {
        foreach (var sink in _sinks)
            sink.NotifyStateChanged(deviceId, state);
    }
}
