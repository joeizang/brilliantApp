using Brilliant.Core.Drafts;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Data;

public sealed class DraftRow
{
    public string StepId { get; set; } = "";
    public string? Code { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// SQLite-backed <see cref="ICodeDraftStore"/>. Give it its own database file, not the event log's: the event log
/// creates its schema with EnsureCreated, which does nothing once any table exists in the file.
/// </summary>
public sealed class SqliteCodeDraftStore : ICodeDraftStore, IDisposable
{
    private sealed class DraftContext(DbContextOptions<DraftContext> options) : DbContext(options)
    {
        public DbSet<DraftRow> Drafts => Set<DraftRow>();

        protected override void OnModelCreating(ModelBuilder b) => b.Entity<DraftRow>(e =>
        {
            e.ToTable("Drafts");
            e.HasKey(x => x.StepId);
            e.Property(x => x.UpdatedAt).HasConversion(v => v.UtcDateTime.Ticks, v => new DateTimeOffset(v, TimeSpan.Zero));
        });
    }

    private readonly DraftContext _db;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    public SqliteCodeDraftStore(string connectionString, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _db = new DraftContext(new DbContextOptionsBuilder<DraftContext>().UseSqlite(connectionString).Options);
        _db.Database.OpenConnection();
        _db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Drafts (StepId TEXT NOT NULL PRIMARY KEY, Code TEXT NULL, UpdatedAt INTEGER NOT NULL);");
    }

    public string? Get(string stepId)
    {
        lock (_gate) return _db.Drafts.AsNoTracking().FirstOrDefault(d => d.StepId == stepId)?.Code;
    }

    public void Save(string stepId, string code) => Upsert(stepId, code);

    public void Reset(string stepId) => Upsert(stepId, null);

    private void Upsert(string stepId, string? code)
    {
        lock (_gate)
        {
            var row = _db.Drafts.Find(stepId);
            if (row is null) _db.Drafts.Add(row = new DraftRow { StepId = stepId });
            row.Code = code;
            row.UpdatedAt = _now();
            _db.SaveChanges();
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>All drafts including reset tombstones (for sync).</summary>
    public IReadOnlyList<CodeDraft> ReadAll()
    {
        lock (_gate) return _db.Drafts.AsNoTracking().ToList().Select(r => new CodeDraft(r.StepId, r.Code, r.UpdatedAt)).ToList();
    }

    public void Dispose() => _db.Dispose();
}
