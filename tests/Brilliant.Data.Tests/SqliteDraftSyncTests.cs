using Brilliant.Core.Drafts;
using Brilliant.Data;
using Microsoft.Data.Sqlite;

namespace Brilliant.Data.Tests;

/// <summary>What the draft store does for sync: tracks what the server hasn't taken, and applies other devices' drafts only if they are newer.</summary>
public sealed class SqliteDraftSyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static CodeDraft Draft(string step, string? code, int minute) => new(step, code, T0.AddMinutes(minute));

    [Fact]
    public void A_local_edit_is_unsynced_until_the_server_has_taken_it()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => T0);
        store.Save("step.a", "v1");
        var sent = store.ReadUnsynced();
        Assert.Equal("v1", Assert.Single(sent).Code);

        store.MarkSynced(sent);

        Assert.Empty(store.ReadUnsynced());
        Assert.Equal("v1", store.Get("step.a"));
    }

    [Fact]
    public void A_reset_is_unsynced_too_so_it_can_reach_the_other_device()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => T0);
        store.Save("step.a", "v1");
        store.MarkSynced(store.ReadUnsynced());

        store.Reset("step.a");

        var tombstone = Assert.Single(store.ReadUnsynced());
        Assert.Equal(("step.a", null), (tombstone.StepId, tombstone.Code));
    }

    [Fact]
    public void An_edit_made_while_a_push_was_in_flight_stays_unsynced()
    {
        var now = T0;
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => now);
        store.Save("step.a", "v1");
        var sent = store.ReadUnsynced();

        now = now.AddSeconds(5);
        store.Save("step.a", "v2");   // typed after the sync read the draft
        store.MarkSynced(sent);

        Assert.Equal("v2", Assert.Single(store.ReadUnsynced()).Code);
    }

    [Fact]
    public void A_newer_draft_from_another_device_replaces_ours_and_is_not_sent_back()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => T0);
        store.Save("step.a", "mine");
        store.MarkSynced(store.ReadUnsynced());

        var applied = store.Merge([Draft("step.a", "theirs", 5)]);

        Assert.Equal(1, applied);
        Assert.Equal("theirs", store.Get("step.a"));
        Assert.Empty(store.ReadUnsynced());
        Assert.Equal(T0.AddMinutes(5), Assert.Single(store.ReadAll()).UpdatedAt);
    }

    [Fact]
    public void An_older_draft_from_another_device_loses_and_ours_stays_to_be_sent()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => T0.AddMinutes(10));
        store.Save("step.a", "mine");

        var applied = store.Merge([Draft("step.a", "theirs", 5)]);

        Assert.Equal(0, applied);
        Assert.Equal("mine", store.Get("step.a"));
        Assert.Equal("mine", Assert.Single(store.ReadUnsynced()).Code);
    }

    [Fact]
    public void A_newer_reset_from_another_device_clears_ours()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => T0);
        store.Save("step.a", "mine");

        store.Merge([Draft("step.a", null, 3)]);

        Assert.Null(store.Get("step.a"));
        Assert.Empty(store.ReadUnsynced());
    }

    [Fact]
    public void A_step_this_device_never_touched_takes_the_other_devices_draft()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:");

        store.Merge([Draft("step.b", "from the phone", 1)]);

        Assert.Equal("from the phone", store.Get("step.b"));
        Assert.Empty(store.ReadUnsynced());
    }

    [Fact]
    public void Merging_the_same_draft_again_changes_nothing()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:");
        var incoming = new[] { Draft("step.a", "x", 1) };
        store.Merge(incoming);

        Assert.Equal(0, store.Merge(incoming));
    }

    [Fact]
    public void A_draft_the_server_echoes_back_unchanged_marks_ours_synced()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => T0);
        store.Save("step.a", "mine");

        store.Merge([Draft("step.a", "mine", 0)]);

        Assert.Empty(store.ReadUnsynced());
    }

    [Fact]
    public void A_tie_goes_to_the_larger_code_whichever_order_the_two_arrive_in()
    {
        using var first = new SqliteCodeDraftStore("Data Source=:memory:");
        using var second = new SqliteCodeDraftStore("Data Source=:memory:");

        first.Merge([Draft("s", "apple", 1)]);
        first.Merge([Draft("s", "banana", 1)]);
        second.Merge([Draft("s", "banana", 1)]);
        second.Merge([Draft("s", "apple", 1)]);
        first.Merge([Draft("s", null, 1)]);   // a reset at the same instant loses to any code

        Assert.Equal("banana", first.Get("s"));
        Assert.Equal("banana", second.Get("s"));
    }

    [Fact]
    public void An_edit_after_a_draft_arrived_wins_even_when_this_devices_clock_is_behind()
    {
        using var store = new SqliteCodeDraftStore("Data Source=:memory:", () => T0);   // this clock says 9:00
        store.Merge([Draft("step.a", "theirs", 30)]);                                   // theirs was written at 9:30

        store.Save("step.a", "mine, typed after reading theirs");

        var mine = Assert.Single(store.ReadUnsynced());
        Assert.True(mine.Supersedes(Draft("step.a", "theirs", 30)));
    }

    [Fact]
    public void A_drafts_file_made_before_sync_existed_is_upgraded_and_everything_in_it_is_unsynced()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("drafts").FullName, "drafts.db");
        try
        {
            using (var old = new SqliteConnection($"Data Source={path}"))
            {
                old.Open();
                using var create = old.CreateCommand();
                create.CommandText = "CREATE TABLE Drafts (StepId TEXT NOT NULL PRIMARY KEY, Code TEXT NULL, UpdatedAt INTEGER NOT NULL); " +
                                     $"INSERT INTO Drafts VALUES ('step.a', 'old work', {T0.UtcTicks});";
                create.ExecuteNonQuery();
            }

            using var store = new SqliteCodeDraftStore($"Data Source={path}");

            Assert.Equal("old work", Assert.Single(store.ReadUnsynced()).Code);
            Assert.Equal("old work", store.Get("step.a"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
