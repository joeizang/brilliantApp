using Brilliant.Core.Progress;
using Brilliant.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Brilliant.Data.Tests;

public sealed class SqliteProgressEventLogTests : IDisposable
{
    private readonly SqliteProgressEventLog _log = new("Data Source=:memory:");
    public void Dispose() => _log.Dispose();

    private static ProgressEvent Evt(string id, string step = "step.a", string type = ProgressEventTypes.StepAnswered) =>
        new(id, "device-1", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), type, "lesson.x", step, """{"correct":true}""");

    [Fact]
    public void Append_then_ReadAll_returns_events_in_append_order_with_all_fields()
    {
        var first = Evt("e1");
        _log.Append(first);
        _log.Append(Evt("e2", "step.b", ProgressEventTypes.StepCompleted));

        var all = _log.ReadAll();

        Assert.Equal(["e1", "e2"], all.Select(e => e.Id));
        Assert.Equal(first, all[0]);
    }

    [Fact]
    public void Empty_log_reads_empty_with_zero_cursor()
    {
        var page = _log.ReadSince(0);
        Assert.Empty(page.Events);
        Assert.Equal(0, page.Cursor);
    }

    [Fact]
    public void ReadSince_returns_only_events_after_the_cursor()
    {
        _log.Append(Evt("e1"));
        var cursor = _log.ReadSince(0).Cursor;
        _log.Append(Evt("e2"));
        _log.Append(Evt("e3"));

        var page = _log.ReadSince(cursor);

        Assert.Equal(["e2", "e3"], page.Events.Select(e => e.Id));
        Assert.Empty(_log.ReadSince(page.Cursor).Events);
        Assert.Equal(page.Cursor, _log.ReadSince(page.Cursor).Cursor);
    }

    [Fact]
    public void Append_is_idempotent_by_event_id()
    {
        Assert.True(_log.Append(Evt("e1")));
        Assert.False(_log.Append(Evt("e1", step: "step.different")));

        var only = Assert.Single(_log.ReadAll());
        Assert.Equal("step.a", only.StepId);
    }

    [Fact]
    public void Events_cannot_be_updated_or_deleted()
    {
        _log.Append(Evt("e1"));
        var ctx = typeof(SqliteProgressEventLog).GetField("_db", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_log) as LocalStoreContext;

        Assert.Throws<SqliteException>(() => ctx!.Database.ExecuteSqlRaw("UPDATE Events SET StepId = 'tampered'"));
        Assert.Throws<SqliteException>(() => ctx!.Database.ExecuteSqlRaw("DELETE FROM Events"));
        Assert.Equal("step.a", Assert.Single(_log.ReadAll()).StepId);
    }

    [Fact]
    public void Events_persist_across_reopening_a_file_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brilliant-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new SqliteProgressEventLog($"Data Source={path}"))
                first.Append(Evt("e1"));
            SqliteConnection.ClearAllPools();

            using var second = new SqliteProgressEventLog($"Data Source={path}");
            Assert.Equal("e1", Assert.Single(second.ReadAll()).Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void DeviceId_is_stable_per_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"brilliant-dev-{Guid.NewGuid():N}");
        try
        {
            var a = DeviceId.GetOrCreate(dir);
            Assert.Equal(a, DeviceId.GetOrCreate(dir));
            Assert.NotEqual(a, DeviceId.GetOrCreate(dir + "-other"));
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(dir + "-other", true);
        }
    }
}
