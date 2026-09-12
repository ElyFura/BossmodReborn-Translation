namespace ShapeCheck;

public sealed class Report
{
    public int Failures { get; private set; }
    public int Warnings { get; private set; }
    private readonly List<string> _notes = [];

    public void Section(string title) => Console.WriteLine($"{Environment.NewLine}-- {title} --");

    public void Check(bool ok, string text)
    {
        if (ok)
        {
            Console.WriteLine("  ok    " + text);
        }
        else
        {
            Console.WriteLine("  FAIL  " + text);
            ++Failures;
        }
    }

    public void Fail(string text)
    {
        Console.WriteLine("  FAIL  " + text);
        ++Failures;
    }

    public void Warn(string text)
    {
        Console.WriteLine("  warn  " + text);
        ++Warnings;
    }

    public void Info(string text) => Console.WriteLine("        " + text);

    public void Note(string text) => _notes.Add(text);

    public int Summarise(bool strict)
    {
        Console.WriteLine();
        foreach (var note in _notes)
        {
            Console.WriteLine(note);
        }

        var failed = Failures > 0 || (strict && Warnings > 0);
        Console.WriteLine();
        Console.WriteLine(Failures == 0 && Warnings == 0
            ? "PASS - every reflection target and seed key matches the installed assembly"
            : $"{(failed ? "FAIL" : "PASS")} - {Failures} failure(s), {Warnings} warning(s)");
        return failed ? 1 : 0;
    }
}
