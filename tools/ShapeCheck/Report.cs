namespace ShapeCheck;

// the writer is injectable so a dump mode can send the report to stderr and keep stdout pure JSON
public sealed class Report(TextWriter? writer = null)
{
    private readonly TextWriter _out = writer ?? Console.Out;

    public int Failures { get; private set; }
    public int Warnings { get; private set; }
    private readonly List<string> _notes = [];

    public void Section(string title) => _out.WriteLine($"{Environment.NewLine}-- {title} --");

    public void Check(bool ok, string text)
    {
        if (ok)
        {
            _out.WriteLine("  ok    " + text);
        }
        else
        {
            _out.WriteLine("  FAIL  " + text);
            ++Failures;
        }
    }

    public void Fail(string text)
    {
        _out.WriteLine("  FAIL  " + text);
        ++Failures;
    }

    public void Warn(string text)
    {
        _out.WriteLine("  warn  " + text);
        ++Warnings;
    }

    public void Info(string text) => _out.WriteLine("        " + text);

    public void Note(string text) => _notes.Add(text);

    public int Summarise(bool strict)
    {
        _out.WriteLine();
        foreach (var note in _notes)
        {
            _out.WriteLine(note);
        }

        var failed = Failures > 0 || (strict && Warnings > 0);
        _out.WriteLine();
        _out.WriteLine(Failures == 0 && Warnings == 0
            ? "PASS - every reflection target and seed key matches the installed assembly"
            : $"{(failed ? "FAIL" : "PASS")} - {Failures} failure(s), {Warnings} warning(s)");
        return failed ? 1 : 0;
    }
}
