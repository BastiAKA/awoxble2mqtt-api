using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace AwoxController.Api.Hubs;

/// <summary>
/// SignalR implementation of <see cref="ILightStateNotifier"/>: broadcasts each state change to every
/// connected client as a <c>StateChanged</c> message. Fire-and-forget — a transport hiccup must never
/// bubble back into the BLE scan loop or a command handler, so failures are swallowed (logged at debug).
/// </summary>
public sealed class SignalRLightStateNotifier : ILightStateNotifier
{
    private readonly IHubContext<LightHub> _hub;
    private readonly ILogger<SignalRLightStateNotifier> _logger;

    public SignalRLightStateNotifier(IHubContext<LightHub> hub, ILogger<SignalRLightStateNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public void NotifyStateChanged(string deviceId, LightState state)
        => _ = BroadcastAsync(deviceId, state);

    private async Task BroadcastAsync(string deviceId, LightState state)
    {
        try
        {
            await _hub.Clients.All.SendAsync("StateChanged", new { deviceId, state });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SignalR StateChanged broadcast for {Device} failed (ignored).", deviceId);
        }
    }
}
