using Brilliant.Core.Apps;
using Brilliant.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server.Data;

public sealed class ServerDb(DbContextOptions<ServerDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();
    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<DraftRecord> Drafts => Set<DraftRecord>();
    public DbSet<PackRecord> Packs => Set<PackRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Device>(d =>
        {
            d.Property(x => x.AppId).HasMaxLength(200).IsRequired().HasDefaultValue(AppStoragePaths.DsaAppId);
            d.HasKey(x => x.Id);
            d.Property(x => x.Name).HasMaxLength(100).IsRequired();
            d.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            d.HasIndex(x => x.TokenHash).IsUnique();
            d.HasIndex(x => x.AppId);
        });
        model.Entity<PairingCode>(c =>
        {
            c.Property(x => x.AppId).HasMaxLength(200).IsRequired().HasDefaultValue(AppStoragePaths.DsaAppId);
            c.HasKey(x => new { x.AppId, x.Hash });
            c.Property(x => x.Hash).HasMaxLength(64);
        });
        model.Entity<EventRecord>(e =>
        {
            e.Property(x => x.AppId).HasMaxLength(200).IsRequired().HasDefaultValue(AppStoragePaths.DsaAppId);
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.Property(x => x.Id).HasMaxLength(EventLimits.MaxIdLength).IsRequired();
            e.HasIndex(x => new { x.AppId, x.Id }).IsUnique();
            e.HasIndex(x => new { x.AppId, x.Seq });
            e.Property(x => x.DeviceId).HasMaxLength(EventLimits.MaxIdLength).IsRequired();
            e.Property(x => x.Type).HasMaxLength(EventLimits.MaxIdLength).IsRequired();
            e.Property(x => x.LessonId).HasMaxLength(EventLimits.MaxIdLength);
            e.Property(x => x.StepId).HasMaxLength(EventLimits.MaxIdLength);
        });
        model.Entity<DraftRecord>(d =>
        {
            d.Property(x => x.AppId).HasMaxLength(200).IsRequired().HasDefaultValue(AppStoragePaths.DsaAppId);
            d.HasKey(x => new { x.AppId, x.StepId });
            d.Property(x => x.StepId).HasMaxLength(EventLimits.MaxIdLength);
            d.HasIndex(x => x.Seq).IsUnique();
            d.HasIndex(x => new { x.AppId, x.Seq });
        });
        model.Entity<PackRecord>(p =>
        {
            p.Property(x => x.AppId).HasMaxLength(200).IsRequired().HasDefaultValue(AppStoragePaths.DsaAppId);
            p.HasKey(x => new { x.AppId, x.PackId, x.Major, x.Minor, x.Patch });
            p.Property(x => x.PackId).HasMaxLength(Brilliant.Core.Content.PackIds.MaxLength);
            p.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        });
    }
}
