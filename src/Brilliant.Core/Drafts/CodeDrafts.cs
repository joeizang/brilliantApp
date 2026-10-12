namespace Brilliant.Core.Drafts;

/// <summary>
/// The learner's in-progress code for one write-code step. A draft with a null <see cref="Code"/> is a tombstone:
/// "reset to the starter", kept (rather than deleted) so a later sync can tell the reset happened after another device's edit.
/// </summary>
public sealed record CodeDraft(string StepId, string? Code, DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Whether this draft beats <paramref name="current"/> (null: nothing there yet). The newer <see cref="UpdatedAt"/> wins; on an exact tie the
    /// larger code does, a tombstone being the smallest, so every device and the server settle on the same draft whatever order they see them in.
    /// </summary>
    public bool Supersedes(CodeDraft? current)
    {
        if (current is null) return true;
        if (UpdatedAt.UtcTicks != current.UpdatedAt.UtcTicks) return UpdatedAt.UtcTicks > current.UpdatedAt.UtcTicks;
        return string.CompareOrdinal(Code, current.Code) > 0;
    }
}

/// <summary>
/// Mutable per-step code drafts. Unlike progress these are not events: only the latest text matters,
/// and when drafts sync between devices the newest <see cref="CodeDraft.UpdatedAt"/> wins.
/// </summary>
public interface ICodeDraftStore
{
    /// <summary>The saved code for the step, or null if none (never edited, or reset).</summary>
    string? Get(string stepId);

    void Save(string stepId, string code);

    /// <summary>Forgets the draft, so the step opens with its starter code again.</summary>
    void Reset(string stepId);
}

/// <summary>
/// What sync needs of the draft store beyond reading and writing. Each local change is "unsynced" until the server has taken it, and a draft
/// from another device is applied only if it is newer than what is here (<see cref="CodeDraft.Supersedes"/>).
/// </summary>
public interface IDraftSyncSource
{
    /// <summary>Drafts (and tombstones) changed on this device since the server last took them.</summary>
    IReadOnlyList<CodeDraft> ReadUnsynced();

    /// <summary>Counts every draft and tombstone as not yet taken by the server: the device is now talking to a different server (or the same one, paired again), which has not been told of any of them.</summary>
    void MarkAllUnsynced();

    /// <summary>The server has <paramref name="sent"/>. A draft edited again since it was read stays unsynced.</summary>
    void MarkSynced(IEnumerable<CodeDraft> sent);

    /// <summary>Applies drafts from the server; returns how many replaced what this device had. A local draft that is newer is kept, and goes up at the next sync.</summary>
    int Merge(IEnumerable<CodeDraft> incoming);
}
