using Microsoft.EntityFrameworkCore;

namespace Brilliant.Server.Data;

public sealed class ServerDb(DbContextOptions<ServerDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();

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
    }
}
