using Brilliant.Core.Apps;
using Brilliant.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Brilliant.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AppScopeMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Existing_household_data_keeps_its_values_and_cursors_in_the_Dsa_scope()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<ServerDb>().UseNpgsql(connection).Options;
        var deviceId = Guid.NewGuid();
        var tokenHash = new string('a', 64);
        var codeHash = new string('b', 64);
        var at = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        const long occurred = 638958382001234567;
        const long updated = 638958382001234568;
        const string payload = "{\"answer\":\"correct\",\"elapsed\":42}";
        const string draftCode = "def f():\n    return 42\n";
        byte[] packBytes = [0, 1, 2, 127, 128, 255];
        var sha = new string('c', 64);

        await using (var old = new ServerDb(options))
        {
            await old.GetService<IMigrator>().MigrateAsync("20261012113041_ContentPacks");
            // Seed the actual previous schema, which has no AppId columns. Do not manufacture already-migrated rows with EF defaults.
            await old.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Devices" ("Id", "Name", "TokenHash", "PairedAt", "LastSeenAt", "RevokedAt")
                VALUES ({deviceId}, {"Original Mac"}, {tokenHash}, {at}, {at.AddMinutes(1)}, NULL)
                """);
            await old.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PairingCodes" ("Hash", "CreatedAt", "ExpiresAt", "UsedAt")
                VALUES ({codeHash}, {at}, {at.AddMinutes(10)}, {at.AddMinutes(2)})
                """);
            await old.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Events" ("Id", "DeviceId", "OccurredAtTicks", "Type", "LessonId", "StepId", "Data", "PushedBy", "ReceivedAt")
                VALUES ({"event.shared"}, {"mac.original"}, {occurred}, {"StepCompleted"}, {"lesson.original"}, {"step.shared"}, {payload}, {deviceId}, {at})
                """);
            await old.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Drafts" ("StepId", "Seq", "Code", "UpdatedAtTicks", "PushedBy", "ReceivedAt")
                VALUES ({"step.shared"}, {42L}, {draftCode}, {updated}, {deviceId}, {at})
                """);
            await old.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Packs" ("PackId", "Major", "Minor", "Patch", "FormatVersion", "Sha256", "Content", "Size", "PublishedBy", "PublishedAt")
                VALUES ({"pack.shared"}, {1}, {2}, {3}, {1}, {sha}, {packBytes}, {packBytes.LongLength}, {deviceId}, {at})
                """);
        }

        await using var db = new ServerDb(options);
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync(); // A startup retry must not reassign data or reset counters.

        var device = await db.Devices.SingleAsync();
        Assert.Equal(AppStoragePaths.DsaAppId, device.AppId);
        Assert.Equal((deviceId, "Original Mac", tokenHash, at, at.AddMinutes(1), null),
            (device.Id, device.Name, device.TokenHash, device.PairedAt, device.LastSeenAt, device.RevokedAt));
        var code = await db.PairingCodes.SingleAsync();
        Assert.Equal((AppStoragePaths.DsaAppId, codeHash, at, at.AddMinutes(10), at.AddMinutes(2)),
            (code.AppId, code.Hash, code.CreatedAt, code.ExpiresAt, code.UsedAt));
        var progress = await db.Events.SingleAsync();
        Assert.Equal((AppStoragePaths.DsaAppId, 1L, "event.shared", "mac.original", occurred, "StepCompleted", "lesson.original", "step.shared", payload, deviceId, at),
            (progress.AppId, progress.Seq, progress.Id, progress.DeviceId, progress.OccurredAtTicks, progress.Type, progress.LessonId, progress.StepId, progress.Data, progress.PushedBy, progress.ReceivedAt));
        var draft = await db.Drafts.SingleAsync();
        Assert.Equal((AppStoragePaths.DsaAppId, "step.shared", 42L, draftCode, updated, deviceId, at),
            (draft.AppId, draft.StepId, draft.Seq, draft.Code, draft.UpdatedAtTicks, draft.PushedBy, draft.ReceivedAt));
        var pack = await db.Packs.SingleAsync();
        Assert.Equal((AppStoragePaths.DsaAppId, "pack.shared", 1, 2, 3, 1, sha, packBytes.LongLength, deviceId, at),
            (pack.AppId, pack.PackId, pack.Major, pack.Minor, pack.Patch, pack.FormatVersion, pack.Sha256, pack.Size, pack.PublishedBy, pack.PublishedAt));
        Assert.Equal(packBytes, pack.Content);

        const string secondApp = "com.example.csharp";
        var secondEvent = new EventRecord { AppId = secondApp, Id = progress.Id, DeviceId = "second.device", Type = progress.Type, LessonId = progress.LessonId, StepId = progress.StepId, OccurredAtTicks = occurred, PushedBy = deviceId, ReceivedAt = at };
        db.Devices.Add(new Device { AppId = secondApp, Id = Guid.NewGuid(), Name = "C# Mac", TokenHash = new string('d', 64), PairedAt = at });
        db.PairingCodes.Add(new PairingCode { AppId = secondApp, Hash = codeHash, CreatedAt = at, ExpiresAt = at.AddMinutes(10) });
        db.Events.Add(secondEvent);
        db.Drafts.Add(new DraftRecord { AppId = secondApp, StepId = draft.StepId, Seq = 43, Code = "return 42;", UpdatedAtTicks = updated, PushedBy = deviceId, ReceivedAt = at });
        db.Packs.Add(new PackRecord { AppId = secondApp, PackId = pack.PackId, Major = pack.Major, Minor = pack.Minor, Patch = pack.Patch, FormatVersion = pack.FormatVersion, Sha256 = sha, Content = packBytes, Size = packBytes.LongLength, PublishedBy = deviceId, PublishedAt = at });
        await db.SaveChangesAsync();

        Assert.Equal(2L, secondEvent.Seq); // The global identity sequence continued from the pre-migration event.
        Assert.Equal(2, await db.PairingCodes.CountAsync(x => x.Hash == codeHash));
        Assert.Equal(2, await db.Events.CountAsync(x => x.Id == progress.Id));
        Assert.Equal(2, await db.Drafts.CountAsync(x => x.StepId == draft.StepId));
        Assert.Equal(2, await db.Packs.CountAsync(x => x.PackId == pack.PackId));
        Assert.Equal(2, await db.Devices.CountAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(42L, (await db.Drafts.SingleAsync(x => x.AppId == AppStoragePaths.DsaAppId)).Seq);
        Assert.Equal(draftCode, (await db.Drafts.SingleAsync(x => x.AppId == AppStoragePaths.DsaAppId)).Code);
        Assert.Equal(tokenHash, (await db.Devices.SingleAsync(x => x.AppId == AppStoragePaths.DsaAppId)).TokenHash);

        var downgrade = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261012113041_ContentPacks"));
        Assert.Equal(PostgresErrorCodes.RaiseException, downgrade.SqlState);
        Assert.Contains("Cannot remove app scoping", downgrade.MessageText);
        Assert.Equal(2, await db.Devices.CountAsync());
        Assert.Equal(2, await db.PairingCodes.CountAsync());
        Assert.Equal(2, await db.Events.CountAsync());
        Assert.Equal(2, await db.Drafts.CountAsync());
        Assert.Equal(2, await db.Packs.CountAsync());
        Assert.Equal(packBytes, (await db.Packs.SingleAsync(x => x.AppId == AppStoragePaths.DsaAppId)).Content);
        Assert.Equal(draftCode, (await db.Drafts.SingleAsync(x => x.AppId == AppStoragePaths.DsaAppId)).Code);
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), name => name.EndsWith("_AppScopedData", StringComparison.Ordinal));
    }
}
