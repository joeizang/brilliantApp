namespace Brilliant.Core.Drafts;

/// <summary>
/// The learner's in-progress code for one write-code step. A draft with a null <see cref="Code"/> is a tombstone:
/// "reset to the starter", kept (rather than deleted) so a later sync can tell the reset happened after another device's edit.
/// </summary>
public sealed record CodeDraft(string StepId, string? Code, DateTimeOffset UpdatedAt);

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
