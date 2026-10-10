using Brilliant.Core.Progress;
using Brilliant.Data;

namespace Brilliant.Data.Tests;

public sealed class SqliteCodeDraftStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("drafts").FullName;
    private string ConnectionString => $"Data Source={Path.Combine(_dir, "drafts.db")}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_step_with_no_draft_reads_null()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:");
        Assert.Null(store.Get("step.a"));
    }

    [Fact]
    public void Saving_again_replaces_the_previous_draft_and_steps_are_independent()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:");
        store.Save("step.a", "v1");
        store.Save("step.a", "v2");
        store.Save("step.b", "other");

        Assert.Equal("v2", store.Get("step.a"));
        Assert.Equal("other", store.Get("step.b"));
    }

    [Fact]
    public void Drafts_survive_closing_and_reopening_the_database()
    {
        using (var store = new SqliteCodeDraftStore(ConnectionString)) store.Save("step.a", "def f():\n    return 1\n");

        using var reopened = new SqliteCodeDraftStore(ConnectionString);
        Assert.Equal("def f():\n    return 1\n", reopened.Get("step.a"));
    }

    [Fact]
    public void Reset_forgets_the_code_but_keeps_a_timestamped_tombstone_for_sync()
    {
        var clock = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => clock);
        store.Save("step.a", "mine");
        clock = clock.AddMinutes(5);
        store.Reset("step.a");

        Assert.Null(store.Get("step.a"));
        var draft = Assert.Single(store.ReadAll());
        Assert.Equal(("step.a", null, clock), (draft.StepId, draft.Code, draft.UpdatedAt));
    }

    [Fact]
    public void Each_save_records_when_it_happened()
    {
        var clock = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => clock);
        store.Save("step.a", "v1");
        clock = clock.AddSeconds(30);
        store.Save("step.a", "v2");

        Assert.Equal(clock, Assert.Single(store.ReadAll()).UpdatedAt);
    }

    [Fact]
    public void Resetting_a_step_that_was_never_edited_is_harmless()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:");
        store.Reset("step.a");
        Assert.Null(store.Get("step.a"));
    }

    [Fact]
    public void Drafts_do_not_disturb_the_event_log_even_when_both_run_in_one_directory()
    {
        using var drafts = new SqliteCodeDraftStore(ConnectionString);
        drafts.Save("step.a", "x");
        using var log = new SqliteProgressEventLog($"Data Source={Path.Combine(_dir, "brilliant.db")}");
        log.Append(new ProgressEvent("e1", "d", DateTimeOffset.UtcNow, ProgressEventTypes.StepAnswered, "lesson.x", "step.a", null));

        Assert.Single(log.ReadAll());
        Assert.Equal("x", drafts.Get("step.a"));
    }
}
