using Microsoft.AspNetCore.SignalR;

namespace AwoxController.Api.Hubs;

/// <summary>
/// SignalR hub for live light-state push, mapped at <c>/hubs/lights</c>. Clients connect and receive
/// <c>StateChanged</c> messages — <c>{ deviceId, state }</c> — whenever a light's state changes. There
/// are no server methods: control still goes through the REST controllers; this hub is push-only.
/// State is broadcast to all clients (a LAN appliance with a handful of viewers), which scales fine to
/// hundreds of lights since the fan-out is in-memory.
/// </summary>
public sealed class LightHub : Hub
{
}
