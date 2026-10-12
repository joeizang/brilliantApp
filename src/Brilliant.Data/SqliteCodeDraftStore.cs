using Brilliant.Core.Drafts;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Data;

public sealed class DraftRow
{
    public string StepId { get; set; } = "";
    public string? Code { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Changed here since the server last took it. Drafts from before sync existed are unsynced too: the server has never seen them.</summary>
    public bool Unsynced { get; set; }
}

/// <summary>
/// SQLite-backed <see cref="ICodeDraftStore"/>. Give it its own database file, not the event log's: the event log
/// creates its schema with EnsureCreated, which does nothing once any table exists in the file.
/// </summary>
public sealed class SqliteCodeDraftStore : ICodeDraftStore, IDraftSyncSource, IDisposable
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
        _db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Drafts (StepId TEXT NOT NULL PRIMARY KEY, Code TEXT NULL, UpdatedAt INTEGER NOT NULL, Unsynced INTEGER NOT NULL DEFAULT 1);");
        // A file made before drafts synced has no such column; what it holds is all unsynced (the default).
        var columns = _db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Drafts')").ToList();
        if (!columns.Contains("Unsynced")) _db.Database.ExecuteSqlRaw("ALTER TABLE Drafts ADD COLUMN Unsynced INTEGER NOT NULL DEFAULT 1;");
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
            // Never earlier than what this device already holds for the step, so an edit made after a draft arrived from elsewhere beats it
            // even when this clock is behind the other device's.
            var at = _now();
            row.Code = code;
            row.UpdatedAt = at.UtcTicks > row.UpdatedAt.UtcTicks ? at : new DateTimeOffset(row.UpdatedAt.UtcTicks + 1, TimeSpan.Zero);
            row.Unsynced = true;
            _db.SaveChanges();
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>All drafts including reset tombstones (for sync).</summary>
    public IReadOnlyList<CodeDraft> ReadAll()
    {
        lock (_gate) return _db.Drafts.AsNoTracking().ToList().Select(r => new CodeDraft(r.StepId, r.Code, r.UpdatedAt)).ToList();
    }

    public IReadOnlyList<CodeDraft> ReadUnsynced()
    {
        lock (_gate) return _db.Drafts.AsNoTracking().Where(d => d.Unsynced).OrderBy(d => d.UpdatedAt).ToList().Select(r => new CodeDraft(r.StepId, r.Code, r.UpdatedAt)).ToList();
    }

    public void MarkAllUnsynced()
    {
        lock (_gate) _db.Database.ExecuteSqlRaw("UPDATE Drafts SET Unsynced = 1;");
    }

    public void MarkSynced(IEnumerable<CodeDraft> sent)
    {
        lock (_gate)
        {
            foreach (var d in sent)
            {
                // Only if it is still the draft that was sent: an edit made during the push must go up at the next sync.
                var row = _db.Drafts.Find(d.StepId);
                if (row is not null && row.UpdatedAt.UtcTicks == d.UpdatedAt.UtcTicks) row.Unsynced = false;
            }
            _db.SaveChanges();
            _db.ChangeTracker.Clear();
        }
    }

    public int Merge(IEnumerable<CodeDraft> incoming)
    {
        lock (_gate)
        {
            var applied = 0;
            foreach (var d in incoming)
            {
                var row = _db.Drafts.Find(d.StepId);
                var local = row is null ? null : new CodeDraft(row.StepId, row.Code, row.UpdatedAt);
                if (!d.Supersedes(local))
                {
                    // The same draft the server holds is synced, however it got there.
                    if (row is not null && d == local) row.Unsynced = false;
                    continue;
                }
                if (row is null) _db.Drafts.Add(row = new DraftRow { StepId = d.StepId });
                (row.Code, row.UpdatedAt, row.Unsynced) = (d.Code, d.UpdatedAt, false);
                applied++;
            }
            _db.SaveChanges();
            _db.ChangeTracker.Clear();
            return applied;
        }
    }

    public void Dispose() => _db.Dispose();
}
