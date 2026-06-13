using AwoxController.Core.Models;

namespace AwoxController.Core.Interfaces;

/// <summary>
/// Persistence for the bulbs and their mesh credentials. Backed by the database so devices can be
/// added (cloud import or scan + blink), renamed, and removed at runtime without editing config.
/// </summary>
public interface IDeviceStore
{
    Task<IReadOnlyList<LampDevice>> GetLampsAsync(CancellationToken ct = default);
    Task<LampDevice?> GetLampByIdAsync(int id, CancellationToken ct = default);
    Task<LampDevice?> GetLampByMacAsync(string mac, CancellationToken ct = default);
    Task<LampDevice?> GetLampByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Inserts a lamp. Throws if the MAC or name already exists.</summary>
    Task<LampDevice> AddLampAsync(LampDevice lamp, CancellationToken ct = default);

    Task UpdateLampAsync(LampDevice lamp, CancellationToken ct = default);
    Task<bool> RemoveLampAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<MeshNetwork>> GetMeshesAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates a mesh by its <see cref="MeshNetwork.Service"/> tag; returns it.</summary>
    Task<MeshNetwork> UpsertMeshAsync(MeshNetwork mesh, CancellationToken ct = default);

    /// <summary>
    /// Bulk import (cloud): upserts the meshes and lamps. Lamps are matched by MAC; existing ones are
    /// updated (mesh id, protocol, mesh link, model) but their friendly name/room are preserved.
    /// Returns (meshesUpserted, lampsAdded, lampsUpdated).
    /// </summary>
    Task<(int meshes, int lampsAdded, int lampsUpdated)> ImportAsync(
        IEnumerable<MeshNetwork> meshes, IEnumerable<LampDevice> lamps, CancellationToken ct = default);
}
