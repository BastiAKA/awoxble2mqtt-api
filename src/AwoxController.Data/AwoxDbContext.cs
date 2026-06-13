using AwoxController.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AwoxController.Data;

/// <summary>EF Core context for the AwoX device registry (MySQL/MariaDB via Pomelo).</summary>
public sealed class AwoxDbContext : DbContext
{
    public AwoxDbContext(DbContextOptions<AwoxDbContext> options) : base(options) { }

    public DbSet<MeshNetwork> Meshes => Set<MeshNetwork>();
    public DbSet<LampDevice> Lamps => Set<LampDevice>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    // A DbSet<T> is "the table for T": querying it (db.Scenes.Where(...)) turns into SQL SELECTs,
    // adding to it (db.Scenes.Add(...)) + SaveChanges turns into INSERTs. We expose SceneItems too so
    // the store can clear a scene's old items directly when it's updated.
    public DbSet<Scene> Scenes => Set<Scene>();
    public DbSet<SceneItem> SceneItems => Set<SceneItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MeshNetwork>(e =>
        {
            e.ToTable("meshes");
            e.HasKey(m => m.Id);
            e.Property(m => m.Service).HasMaxLength(32).IsRequired();
            e.Property(m => m.MeshName).HasMaxLength(64).IsRequired();
            e.Property(m => m.MeshPassword).HasMaxLength(64).IsRequired();
            e.Property(m => m.MeshKey).HasMaxLength(128);
            e.HasIndex(m => m.Service).IsUnique();
        });

        b.Entity<LampDevice>(e =>
        {
            e.ToTable("lamps");
            e.HasKey(l => l.Id);
            e.Property(l => l.Name).HasMaxLength(128).IsRequired();
            e.Property(l => l.Mac).HasMaxLength(17).IsRequired();
            e.Property(l => l.Model).HasMaxLength(64);
            e.Property(l => l.DeviceType).HasMaxLength(64);
            e.Property(l => l.Room).HasMaxLength(64);
            e.Property(l => l.LastState).HasMaxLength(512);
            e.HasIndex(l => l.Mac).IsUnique();
            e.HasIndex(l => l.Name).IsUnique();
            e.HasOne(l => l.Mesh)
                .WithMany(m => m.Lamps)
                .HasForeignKey(l => l.MeshNetworkId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AppSetting>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(64);
            e.Property(s => s.Value).HasMaxLength(512).IsRequired();
            e.Property(s => s.Description).HasMaxLength(256);
        });

        b.Entity<Scene>(e =>
        {
            e.ToTable("scenes");
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).HasMaxLength(128).IsRequired();
            e.HasIndex(s => s.Name).IsUnique();

            // The one-to-many: one scene has many items. WithOne(i => i.Scene) names the back-reference,
            // HasForeignKey(i => i.SceneId) is the FK column on scene_items, and Cascade means deleting a
            // scene deletes its items automatically (no orphan rows).
            e.HasMany(s => s.Items)
                .WithOne(i => i.Scene!)
                .HasForeignKey(i => i.SceneId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SceneItem>(e =>
        {
            e.ToTable("scene_items");
            e.HasKey(i => i.Id);
            e.Property(i => i.DesiredState).HasMaxLength(512).IsRequired();

            // Each item also points at a lamp. If a lamp is deleted, drop the scene entries that referenced
            // it (a scene can't target a lamp that no longer exists). No navigation back from LampDevice —
            // a lamp doesn't need to know which scenes use it — so WithMany() is left empty.
            e.HasOne(i => i.Lamp)
                .WithMany()
                .HasForeignKey(i => i.LampDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
