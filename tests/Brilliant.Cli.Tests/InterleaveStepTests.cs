using Brilliant.Core.Content;

namespace Brilliant.Cli.Tests;

/// <summary>The <c>interleave</c> step as authors write it: lost-update and deadlock puzzles checked at pack time (#162).</summary>
public sealed class InterleaveStepTests : IDisposable
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), "brilliant-interleave-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_repository, "content", "sample");
    private string AppManifest => Path.Combine(_repository, "apps", "sample", "manifest.yaml");

    private const string LostUpdate = """
          - id: step.sample.lost-update
            type: interleave
            title: Lose an update
            prompt: Two threads each add one to the counter. Find an order that leaves it at 1.
            language: csharp
            shared:
              - name: counter
                value: 0
            threads:
              - name: A
                ops:
                  - code: var t = counter;
                    do: t = counter
                  - code: counter = t + 1;
                    do: counter = t + 1
              - name: B
                ops:
                  - code: var t = counter;
                    do: t = counter
                  - code: counter = t + 1;
                    do: counter = t + 1
            invariant:
              kind: final-value
              description: counter is 2 once both threads have finished
              variable: counter
              expected: 2
            breakingSchedule: [A, B, A, B]
            replays:
              - label: One after the other
                schedule: [A, A, B, B]
            explanation: Both threads read 0 before either wrote, so both wrote 1.
            hints:
              - What if B reads before A has written?
        """;

    private const string Deadlock = """
          - id: step.sample.deadlock
            type: interleave
            title: Deadlock
            prompt: Make both threads wait forever.
            threads:
              - name: A
                ops:
                  - { code: 'lock (first) {', do: lock first }
                  - { code: 'lock (second) {', do: lock second }
                  - { code: '}', do: unlock second }
                  - { code: '}', do: unlock first }
              - name: B
                ops:
                  - { code: 'lock (second) {', do: lock second }
                  - { code: 'lock (first) {', do: lock first }
                  - { code: '}', do: unlock first }
                  - { code: '}', do: unlock second }
            invariant:
              kind: no-deadlock
              description: both threads always finish
            breakingSchedule: [A, B]
            explanation: A holds first and wants second; B holds second and wants first.
        """;

    private void WritePack(string steps, string stepTypes = "[explain, interleave]", string? reviewItems = null)
    {
        Write("apps/sample/manifest.yaml", $$"""
            appId: example.interleave
            displayName: Interleave
            accent:
              dark: '#112233'
              light: '#445566'
              onDark: '#ffffff'
              onLight: '#000000'
            contentPack: sample.pack.zip
            runtimes: [csharp]
            stepTypes: {{stepTypes}}
            """);
        Write("content/sample/pack.yaml", "id: pack.interleave\nversion: 1.0.0\ntargetFramework: net10.0\nlangVersion: '14'\n");
        Write("content/sample/tracks/one/track.yaml", "id: track.interleave\ntitle: Interleave\n");
        var review = reviewItems is null ? "" : "concepts:\n  - id: concept.sample\n    title: Sample concept\nreviewItems:\n" + reviewItems + "\n";
        Write("content/sample/tracks/one/threads/lesson.yaml", "id: lesson.interleave\ntitle: Threads\n" + review + "steps:\n" + steps + "\n");
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_repository, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { try { Directory.Delete(_repository, true); } catch (IOException) { } }

    private IReadOnlyList<string> Errors() =>
        ContentValidator.Validate(Root, appManifestPath: AppManifest).Errors.Select(e => e.Message).ToList();

    [Fact]
    public void A_lost_update_puzzle_is_valid()
    {
        WritePack(LostUpdate);

        Assert.Empty(Errors());
    }

    [Fact]
    public void A_deadlock_puzzle_is_valid()
    {
        WritePack(Deadlock);

        Assert.Empty(Errors());
    }

    [Fact]
    public void Both_steps_pack_with_their_threads_invariant_and_schedules()
    {
        WritePack(LostUpdate + "\n" + Deadlock);

        var bytes = ContentPacker.PackToBytes(Root, out var report, appManifestPath: AppManifest);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
        var steps = ContentPackFormat.Load(new MemoryStream(bytes!)).Lessons.Single().Steps;
        var lost = Assert.IsType<InterleaveStep>(steps[0]);
        Assert.Equal(["A", "B"], lost.Threads.Select(t => t.Name));
        Assert.Equal(new InterleaveInvariant("final-value", "counter is 2 once both threads have finished", "counter", 2), lost.Invariant);
        Assert.Equal(["A", "B", "A", "B"], lost.BreakingSchedule);
        Assert.Equal("One after the other", Assert.Single(lost.Replays).Label);
        Assert.Single(lost.Hints);
        Assert.True(InterleaveMachine.Evaluate(lost, lost.BreakingSchedule).BreaksInvariant);
        Assert.Equal("no-deadlock", Assert.IsType<InterleaveStep>(steps[1]).Invariant.Kind);
    }

    [Fact]
    public void An_interleave_step_can_be_a_review_question()
    {
        WritePack(LostUpdate, reviewItems: "  - id: review.sample.lost\n    concept: concept.sample\n    step: step.sample.lost-update");

        Assert.Empty(Errors());
    }

    [Fact]
    public void The_app_manifest_must_enable_the_step_type()
    {
        WritePack(LostUpdate, stepTypes: "[explain]");

        Assert.Contains(Errors(), e => e.Contains("step type 'interleave' is not enabled"));
    }

    [Fact]
    public void A_breaking_schedule_that_keeps_the_invariant_is_rejected()
    {
        WritePack(LostUpdate.Replace("breakingSchedule: [A, B, A, B]", "breakingSchedule: [A, A, B, B]"));

        Assert.Contains(Errors(), e => e.Contains("the 'breakingSchedule' does not break the invariant"));
    }

    [Fact]
    public void A_puzzle_that_already_fails_without_interleaving_is_rejected()
    {
        WritePack(LostUpdate.Replace("expected: 2", "expected: 5"));

        Assert.Contains(Errors(), e => e.Contains("running the threads one after another (A then B) already breaks the invariant"));
    }

    [Fact]
    public void Lines_outside_the_machine_language_are_reported_with_their_position()
    {
        WritePack(LostUpdate.Replace("do: counter = t + 1\n", "do: counter++\n"));

        Assert.Contains(Errors(), e => e.Contains("threads[0], ops[1]: 'do'") && e.Contains("counter++"));
    }

    [Fact]
    public void A_local_must_be_loaded_before_it_is_stored()
    {
        // Thread A's first op (the load) goes, so its store uses a local it never loaded.
        WritePack(DropLines(LostUpdate, "code: var t = counter;", "do: t = counter"));

        Assert.Contains(Errors(), e => e.Contains("'t' is used before this thread loads it"));
    }

    [Fact]
    public void A_thread_that_ends_holding_a_lock_is_rejected()
    {
        WritePack(DropLines(Deadlock, "do: unlock first }"));

        Assert.Contains(Errors(), e => e.Contains("ends still holding"));
    }

    [Fact]
    public void Unlocking_a_lock_that_is_not_held_is_rejected()
    {
        WritePack(Deadlock.Replace("do: unlock first }", "do: unlock second }"));

        Assert.NotEmpty(Errors());
    }

    [Fact]
    public void A_replay_that_names_an_unknown_thread_cannot_be_played()
    {
        WritePack(LostUpdate.Replace("schedule: [A, A, B, B]", "schedule: [A, C]"));

        Assert.Contains(Errors(), e => e.Contains("replays[0] cannot be played") && e.Contains("no thread 'C'"));
    }

    [Fact]
    public void The_invariant_must_name_a_shared_variable_and_an_expected_value()
    {
        WritePack(DropLines(LostUpdate.Replace("variable: counter", "variable: total"), "expected: 2"));

        var errors = Errors();
        Assert.Contains(errors, e => e.Contains("needs a 'variable' that is one of the shared variables"));
        Assert.Contains(errors, e => e.Contains("needs the 'expected' value"));
    }

    [Fact]
    public void An_interleave_step_needs_an_explanation()
    {
        WritePack(DropLines(LostUpdate, "explanation:"));

        Assert.Contains(Errors(), e => e.Contains("'explanation'"));
    }

    [Fact]
    public void An_interleave_step_needs_two_or_three_threads()
    {
        var oneThread = LostUpdate[..LostUpdate.IndexOf("      - name: B", StringComparison.Ordinal)] + LostUpdate[LostUpdate.IndexOf("    invariant:", StringComparison.Ordinal)..];

        WritePack(oneThread);

        Assert.Contains(Errors(), e => e.Contains("needs 2 or 3 threads"));
    }

    [Fact]
    public void Question_step_fields_are_refused()
    {
        WritePack(LostUpdate.Replace("    language: csharp\n", "    language: csharp\n    accepted: ['1']\n"));

        Assert.Contains(Errors(), e => e.Contains("an interleave step is answered by building a schedule"));
    }

    /// <summary>Removes the first line containing each of <paramref name="markers"/>.</summary>
    private static string DropLines(string yaml, params string[] markers)
    {
        var lines = yaml.Split('\n').ToList();
        foreach (var marker in markers) lines.RemoveAt(lines.FindIndex(l => l.Contains(marker, StringComparison.Ordinal)));
        return string.Join('\n', lines);
    }
}
