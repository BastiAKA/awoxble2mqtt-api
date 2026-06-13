using AwoxController.Core.Models;

namespace AwoxController.Core.Interfaces;

/// <summary>Persistence for scenes (a named set of lamps + the state each should take). DB-backed.</summary>
public interface ISceneStore
{
    /// <summary>All scenes with their items + each item's lamp loaded.</summary>
    Task<IReadOnlyList<Scene>> GetScenesAsync(CancellationToken ct = default);

    /// <summary>One scene by id, with its items + lamps; null if not found.</summary>
    Task<Scene?> GetSceneByIdAsync(int id, CancellationToken ct = default);

    /// <summary>Inserts a scene (and its items). Throws if the name already exists.</summary>
    Task<Scene> AddSceneAsync(Scene scene, CancellationToken ct = default);

    /// <summary>
    /// Replaces a scene's name and its full item set (delete-and-re-add — simplest correct way to sync a
    /// child collection). Returns the saved scene, or null if the id doesn't exist.
    /// </summary>
    Task<Scene?> UpdateSceneAsync(int id, string name, IEnumerable<SceneItem> items, CancellationToken ct = default);

    Task<bool> RemoveSceneAsync(int id, CancellationToken ct = default);
}
