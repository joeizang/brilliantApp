using Brilliant.Core.Progress;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Data;

/// <summary>SQLite-backed <see cref="IProgressEventLog"/>. Database triggers reject UPDATE and DELETE, so events are immutable.</summary>
public sealed class SqliteProgressEventLog : IProgressEventLog, IDisposable
{
    private readonly LocalStoreContext _db;

    /// <param name="connectionString">e.g. <c>Data Source=/path/brilliant.db</c> or <c>Data Source=:memory:</c>.</param>
    public SqliteProgressEventLog(string connectionString)
    {
        var options = new DbContextOptionsBuilder<LocalStoreContext>().UseSqlite(connectionString).Options;
        _db = new LocalStoreContext(options);
        // An in-memory database lives and dies with its connection, so open it for the context's lifetime.
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS Events_no_update BEFORE UPDATE ON Events
            BEGIN SELECT RAISE(ABORT, 'Progress events are immutable'); END;
            """);
        _db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS Events_no_delete BEFORE DELETE ON Events
            BEGIN SELECT RAISE(ABORT, 'Progress events are immutable'); END;
            """);
    }

    public bool Append(ProgressEvent e)
    {
        if (_db.Events.AsNoTracking().Any(x => x.Id == e.Id)) return false;
        _db.Events.Add(new EventRow
        {
            Id = e.Id, DeviceId = e.DeviceId, OccurredAt = e.OccurredAt, Type = e.Type,
            LessonId = e.LessonId, StepId = e.StepId, Data = e.Data,
        });
        try
        {
            _db.SaveChanges();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Lost a race on the unique ID index: treat as already present.
            _db.ChangeTracker.Clear();
            return false;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    public IReadOnlyList<ProgressEvent> ReadAll() => ReadSince(0).Events;

    public EventPage ReadSince(long cursor)
    {
        var rows = _db.Events.AsNoTracking().Where(x => x.Seq > cursor).OrderBy(x => x.Seq).ToList();
        var events = rows.Select(r => new ProgressEvent(r.Id, r.DeviceId, r.OccurredAt, r.Type, r.LessonId, r.StepId, r.Data)).ToList();
        return new EventPage(events, rows.Count == 0 ? cursor : rows[^1].Seq);
    }

    public void Dispose() => _db.Dispose();
}
