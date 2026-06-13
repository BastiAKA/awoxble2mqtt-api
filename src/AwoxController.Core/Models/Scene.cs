namespace AwoxController.Core.Models;

/// <summary>
/// A named scene: a saved set of lamps with the state each should take when the scene is applied
/// (e.g. "Filmabend" = TV-lamp dim red, ceiling off). Applying a scene fans the desired states out to
/// every lamp in it.
///
/// EF-Core mapping (configured in <c>AwoxDbContext.OnModelCreating</c>):
///   • This is the "one" side of a one-to-many relationship. One <see cref="Scene"/> has many
///     <see cref="SceneItem"/>s. The <see cref="Items"/> list is a <em>navigation property</em>: EF
///     fills it when you ask for it (<c>.Include(s =&gt; s.Items)</c>) and writes the children when you
///     save the parent.
///   • We do NOT embed full <see cref="LightDevice"/> copies. A scene only needs to know <em>which</em>
///     lamp and <em>what</em> state — see <see cref="SceneItem"/>. That keeps it generic: the desired
///     state is the same capability-based shape every device already uses, not a per-device-type class.
/// </summary>
public sealed class Scene
{
    public int Id { get; set; }

    /// <summary>User-facing name, unique (e.g. "Filmabend").</summary>
    public string Name { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The lamps in this scene and their desired state. EF's navigation to the "many" side.</summary>
    public List<SceneItem> Items { get; set; } = new();
}

/// <summary>
/// One lamp's entry in a <see cref="Scene"/>: which lamp, and the state it should take. This is the
/// "many" side of the one-to-many.
///
/// EF mapping:
///   • <see cref="SceneId"/> + <see cref="Scene"/> are the foreign key back to the owning scene. Deleting
///     a scene cascade-deletes its items.
///   • <see cref="LampDeviceId"/> + <see cref="Lamp"/> point at the target lamp in the <c>lamps</c> table.
///     If the lamp is deleted, its scene entries are removed too (cascade) — a scene can't reference a
///     lamp that no longer exists.
///   • The desired state is stored as JSON in <see cref="DesiredState"/> (same <c>{on,brightness,
///     colorBrightness,color,colorTemp}</c> shape as <see cref="LampDevice.LastState"/>). We keep it as
///     one column rather than five typed columns to mirror the existing LastState pattern and stay
///     capability-generic; we never need to query "which scenes set lamp X red", so a JSON blob is fine.
/// </summary>
public sealed class SceneItem
{
    public int Id { get; set; }

    public int SceneId { get; set; }
    public Scene? Scene { get; set; }

    public int LampDeviceId { get; set; }
    public LampDevice? Lamp { get; set; }

    /// <summary>Desired state as JSON, e.g. <c>{"on":true,"brightness":40,"color":{"r":255,"g":0,"b":0}}</c>.</summary>
    public string DesiredState { get; set; } = "{}";
}
