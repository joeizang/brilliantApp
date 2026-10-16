using System.Text;
using Brilliant.Code.CSharp;

namespace Brilliant.Runtime.CSharp;

internal sealed class SubmissionOutput : TextWriter
{
    public const int MaximumCharacters = 16_384;
    public const string LimitMessage = "The code exceeded its printed-output limit.";
    private readonly StringBuilder _text = new();
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value)
    {
        if (_text.Length >= MaximumCharacters) throw new BudgetExceededException(LimitMessage);
        _text.Append(value);
    }
    public override void Write(string? value)
    {
        if (value is null) return;
        if (value.Length > MaximumCharacters - _text.Length) throw new BudgetExceededException(LimitMessage);
        _text.Append(value);
    }
    public override string ToString() => _text.ToString();
}
