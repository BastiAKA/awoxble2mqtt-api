namespace AwoxController.Core.Interfaces;

/// <summary>
/// Marker for a concrete transport backend (Zigbee, BLE, …). Backends register as
/// <see cref="ILightBackend"/> so the <c>CompositeLightService</c> can collect them all
/// without capturing itself. Application code keeps depending on <see cref="ILightService"/>.
/// </summary>
public interface ILightBackend : ILightService
{
}
