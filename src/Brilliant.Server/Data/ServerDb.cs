using Brilliant.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server.Data;

public sealed class ServerDb(DbContextOptions<ServerDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();
    public DbSet<EventRecord> Events => Set<EventRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Device>(d =>
        {
            d.HasKey(x => x.Id);
            d.Property(x => x.Name).HasMaxLength(100).IsRequired();
            d.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            d.HasIndex(x => x.TokenHash).IsUnique();
        });
        model.Entity<PairingCode>(c =>
        {
            c.HasKey(x => x.Hash);
            c.Property(x => x.Hash).HasMaxLength(64);
        });
        model.Entity<EventRecord>(e =>
        {
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.Property(x => x.Id).HasMaxLength(EventLimits.MaxIdLength).IsRequired();
            e.HasIndex(x => x.Id).IsUnique();
            e.Property(x => x.DeviceId).HasMaxLength(EventLimits.MaxIdLength).IsRequired();
            e.Property(x => x.Type).HasMaxLength(EventLimits.MaxIdLength).IsRequired();
            e.Property(x => x.LessonId).HasMaxLength(EventLimits.MaxIdLength);
            e.Property(x => x.StepId).HasMaxLength(EventLimits.MaxIdLength);
        });
    }
}
