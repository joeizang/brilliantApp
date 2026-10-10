namespace Brilliant.Core.Content;

/// <summary>A line the learner has placed: which solution line it is (<see cref="Piece"/>, its index in <see cref="ParsonsStep.Lines"/>) and the indentation they gave it.</summary>
public sealed record ParsonsPlacement(int Piece, int Level);

public enum RowVerdict
{
    /// <summary>The right line, at the right indentation, in the right place.</summary>
    Correct,

    /// <summary>The right line for this position, but indented too much or too little.</summary>
    WrongIndent,

    /// <summary>A different line belongs here.</summary>
    WrongLine,
}

public sealed record ParsonsResult(IReadOnlyList<RowVerdict> Rows)
{
    public bool IsCorrect => Rows.All(r => r == RowVerdict.Correct);
    public int CorrectCount => Rows.Count(r => r == RowVerdict.Correct);
}

/// <summary>Pure evaluation of a Parsons arrangement: order and indentation, row by row.</summary>
public static class ParsonsEvaluator
{
    /// <summary>
    /// Compares each row with the solution line at the same position. Lines are compared by their text, not their identity, so two
    /// identical lines may be swapped freely.
    /// </summary>
    public static ParsonsResult Evaluate(ParsonsStep step, IReadOnlyList<ParsonsPlacement> arrangement)
    {
        if (arrangement.Count != step.Lines.Count)
            throw new ArgumentException($"Expected {step.Lines.Count} lines but got {arrangement.Count}.", nameof(arrangement));
        if (arrangement.Any(p => p.Piece < 0 || p.Piece >= step.Lines.Count))
            throw new ArgumentOutOfRangeException(nameof(arrangement), "A placed line does not exist in this step.");

        return new ParsonsResult(arrangement.Select((placed, i) =>
        {
            var expected = step.Lines[i];
            if (step.Lines[placed.Piece].Text != expected.Text) return RowVerdict.WrongLine;
            return placed.Level == expected.Level ? RowVerdict.Correct : RowVerdict.WrongIndent;
        }).ToList());
    }
}

/// <summary>
/// The learner's working arrangement of a Parsons step: every line in a single list, each with an indentation level. It starts shuffled
/// and flat; the learner moves lines and changes their indentation. Pure state and rules, no UI.
/// </summary>
public sealed class ParsonsBoard
{
    private readonly ParsonsStep _step;
    private readonly List<ParsonsPlacement> _rows;

    /// <summary>The lines in their current order, with their indentation.</summary>
    public IReadOnlyList<ParsonsPlacement> Rows => _rows;

    /// <summary>The deepest indentation any line of the solution has; lines can't be indented further than that.</summary>
    public int MaxLevel => _step.Lines.Max(l => l.Level);

    public ParsonsStep Step => _step;

    /// <summary>A board holding the lines in the given order (indexes into the solution), all unindented.</summary>
    public ParsonsBoard(ParsonsStep step, IEnumerable<int> order)
    {
        _step = step;
        _rows = order.Select(i => new ParsonsPlacement(i, 0)).ToList();
        if (_rows.Count != step.Lines.Count || _rows.Select(r => r.Piece).Order().Where((p, i) => p != i).Any())
            throw new ArgumentException("The order must contain every line of the step exactly once.", nameof(order));
    }

    /// <summary>A random order that isn't already the solution's, unless the step has no other order.</summary>
    public static ParsonsBoard Shuffled(ParsonsStep step, Random? random = null)
    {
        random ??= Random.Shared;
        var order = Enumerable.Range(0, step.Lines.Count).ToArray();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            random.Shuffle(order);
            if (order.Select((p, i) => step.Lines[p].Text != step.Lines[i].Text).Any(differs => differs)) break;
        }
        return new ParsonsBoard(step, order);
    }

    /// <summary>Moves the line at <paramref name="from"/> so it ends up at index <paramref name="to"/>. Returns false if nothing changed.</summary>
    public bool Move(int from, int to)
    {
        if (from < 0 || from >= _rows.Count || to < 0 || to >= _rows.Count || from == to) return false;
        var row = _rows[from];
        _rows.RemoveAt(from);
        _rows.Insert(to, row);
        return true;
    }

    /// <summary>Indents (positive) or outdents (negative) a line, never below 0 or beyond <see cref="MaxLevel"/>. Returns false if nothing changed.</summary>
    public bool Indent(int row, int delta)
    {
        if (row < 0 || row >= _rows.Count) return false;
        var level = Math.Clamp(_rows[row].Level + delta, 0, MaxLevel);
        if (level == _rows[row].Level) return false;
        _rows[row] = _rows[row] with { Level = level };
        return true;
    }

    public ParsonsResult Check() => ParsonsEvaluator.Evaluate(_step, _rows);
}
