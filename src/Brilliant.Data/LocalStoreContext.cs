using Microsoft.EntityFrameworkCore;

namespace Brilliant.Data;

public sealed class EventRow
{
    /// <summary>Local append-order sequence; used as the read cursor. Not the event's global identity.</summary>
    public long Seq { get; set; }
    public string Id { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public string Type { get; set; } = "";
    public string LessonId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string? Data { get; set; }
}

public sealed class LocalStoreContext(DbContextOptions<LocalStoreContext> options) : DbContext(options)
{
    public DbSet<EventRow> Events => Set<EventRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<EventRow>(e =>
        {
            e.ToTable("Events");
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Id).IsUnique();
            // Stored as ticks-in-UTC text so ordering/comparison stays well-defined in SQLite.
            e.Property(x => x.OccurredAt).HasConversion(v => v.UtcDateTime.Ticks, v => new DateTimeOffset(v, TimeSpan.Zero));
        });
    }
}
