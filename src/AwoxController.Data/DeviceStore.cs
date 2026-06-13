using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AwoxController.Data;

/// <summary>EF Core implementation of <see cref="IDeviceStore"/>.</summary>
public sealed class DeviceStore : IDeviceStore
{
    private readonly AwoxDbContext _db;

    public DeviceStore(AwoxDbContext db) => _db = db;

    public async Task<IReadOnlyList<LampDevice>> GetLampsAsync(CancellationToken ct = default)
        => await _db.Lamps.Include(l => l.Mesh).AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);

    public Task<LampDevice?> GetLampByIdAsync(int id, CancellationToken ct = default)
        => _db.Lamps.Include(l => l.Mesh).FirstOrDefaultAsync(l => l.Id == id, ct);

    public Task<LampDevice?> GetLampByMacAsync(string mac, CancellationToken ct = default)
        => _db.Lamps.Include(l => l.Mesh).FirstOrDefaultAsync(l => l.Mac == mac, ct);

    public Task<LampDevice?> GetLampByNameAsync(string name, CancellationToken ct = default)
        => _db.Lamps.Include(l => l.Mesh).FirstOrDefaultAsync(l => l.Name == name, ct);

    public async Task<LampDevice> AddLampAsync(LampDevice lamp, CancellationToken ct = default)
    {
        _db.Lamps.Add(lamp);
        await _db.SaveChangesAsync(ct);
        return lamp;
    }

    public async Task UpdateLampAsync(LampDevice lamp, CancellationToken ct = default)
    {
        _db.Lamps.Update(lamp);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveLampAsync(int id, CancellationToken ct = default)
    {
        var lamp = await _db.Lamps.FindAsync([id], ct);
        if (lamp is null) return false;
        _db.Lamps.Remove(lamp);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<MeshNetwork>> GetMeshesAsync(CancellationToken ct = default)
        => await _db.Meshes.AsNoTracking().ToListAsync(ct);

    public async Task<MeshNetwork> UpsertMeshAsync(MeshNetwork mesh, CancellationToken ct = default)
    {
        var existing = await _db.Meshes.FirstOrDefaultAsync(m => m.Service == mesh.Service, ct);
        if (existing is null)
        {
            _db.Meshes.Add(mesh);
            await _db.SaveChangesAsync(ct);
            return mesh;
        }

        existing.MeshName = mesh.MeshName;
        existing.MeshPassword = mesh.MeshPassword;
        existing.MeshKey = mesh.MeshKey;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<(int meshes, int lampsAdded, int lampsUpdated)> ImportAsync(
        IEnumerable<MeshNetwork> meshes, IEnumerable<LampDevice> lamps, CancellationToken ct = default)
    {
        var meshByService = new Dictionary<string, MeshNetwork>(StringComparer.OrdinalIgnoreCase);
        var meshCount = 0;
        foreach (var m in meshes)
        {
            var saved = await UpsertMeshAsync(m, ct);
            meshByService[saved.Service] = saved;
            meshCount++;
        }

        var existingLamps = await _db.Lamps.ToListAsync(ct);
        var byMac = existingLamps.ToDictionary(l => l.Mac, StringComparer.OrdinalIgnoreCase);
        // Names must stay unique (used as the API id). The cloud can return duplicates (e.g. "Flur"
        // and "Flur "), so de-duplicate new names with a numeric suffix.
        var usedNames = new HashSet<string>(existingLamps.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);

        int added = 0, updated = 0;
        foreach (var lamp in lamps)
        {
            // resolve the mesh FK from the in-memory service tag carried on lamp.Mesh
            var service = lamp.Mesh?.Service;
            if (service is not null && meshByService.TryGetValue(service, out var mesh))
                lamp.MeshNetworkId = mesh.Id;
            lamp.Mesh = null;

            if (byMac.TryGetValue(lamp.Mac, out var existing))
            {
                // preserve user-chosen Name/Room; refresh the technical fields
                existing.MeshId = lamp.MeshId;
                existing.Protocol = lamp.Protocol;
                existing.Model = lamp.Model;
                if (lamp.MeshNetworkId is not null) existing.MeshNetworkId = lamp.MeshNetworkId;
                updated++;
                continue;
            }

            lamp.Name = UniqueName(lamp.Name, usedNames);
            usedNames.Add(lamp.Name);
            await _db.Lamps.AddAsync(lamp, ct);
            byMac[lamp.Mac] = lamp;
            added++;
        }
        await _db.SaveChangesAsync(ct);
        return (meshCount, added, updated);
    }

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        baseName = string.IsNullOrWhiteSpace(baseName) ? "Lamp" : baseName.Trim();
        if (!used.Contains(baseName)) return baseName;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} {n}";
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
