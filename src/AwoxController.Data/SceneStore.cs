using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AwoxController.Data;

/// <summary>EF Core implementation of <see cref="ISceneStore"/>.</summary>
public sealed class SceneStore : ISceneStore
{
    private readonly AwoxDbContext _db;

    public SceneStore(AwoxDbContext db) => _db = db;

    // .Include pulls the related rows in the same query (a JOIN): without it, scene.Items would be empty
    // because EF only loads what you ask for. .ThenInclude reaches one level deeper (item → its lamp).
    // .AsNoTracking skips EF's change-tracking — right for read-only queries (faster, less memory).
    public async Task<IReadOnlyList<Scene>> GetScenesAsync(CancellationToken ct = default)
        => await _db.Scenes
            .Include(s => s.Items).ThenInclude(i => i.Lamp)
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

    public Task<Scene?> GetSceneByIdAsync(int id, CancellationToken ct = default)
        => _db.Scenes
            .Include(s => s.Items).ThenInclude(i => i.Lamp)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<Scene> AddSceneAsync(Scene scene, CancellationToken ct = default)
    {
        // Adding the parent also inserts its Items: EF sees them in the navigation collection and
        // INSERTs them with the new SceneId filled in automatically.
        _db.Scenes.Add(scene);
        await _db.SaveChangesAsync(ct);
        return scene;
    }

    public async Task<Scene?> UpdateSceneAsync(int id, string name, IEnumerable<SceneItem> items, CancellationToken ct = default)
    {
        // Load WITH tracking (no AsNoTracking) so EF watches our changes and writes them on SaveChanges.
        var scene = await _db.Scenes.Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (scene is null) return null;

        scene.Name = name;

        // Simplest correct way to sync a child collection: drop the old items and add the new set. EF
        // turns this into DELETEs + INSERTs in one transaction on SaveChanges.
        _db.SceneItems.RemoveRange(scene.Items);
        scene.Items = items.ToList();

        await _db.SaveChangesAsync(ct);
        return scene;
    }

    public async Task<bool> RemoveSceneAsync(int id, CancellationToken ct = default)
    {
        var scene = await _db.Scenes.FindAsync([id], ct);
        if (scene is null) return false;
        _db.Scenes.Remove(scene); // cascade (configured in OnModelCreating) removes its items too
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
