using Sqlity.Query;

namespace Sqlity.Query.Tests;

/// <summary>
/// Verifies that parse errors include a [line:column] position prefix so
/// developers can quickly locate the offending token in their SQL text.
/// </summary>
public sealed class SourcePositionTests
{
    private static QueryEngine CreateEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        return new QueryEngine(path);
    }

    // ── Single-line errors ───────────────────────────────────────────────────

    [Fact]
    public void SingleLineError_IncludesLineAndColumn()
    {
        using var engine = CreateEngine();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Execute("FROQ users"));

        Assert.Matches(@"\[1:\d+\]", ex.Message);
    }

    [Fact]
    public void MissingTableName_IncludesPosition()
    {
        using var engine = CreateEngine();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Execute("SELECT * FROM"));

        Assert.Matches(@"\[\d+:\d+\]", ex.Message);
    }

    // ── Multi-line errors ────────────────────────────────────────────────────

    [Fact]
    public void ErrorOnSecondLine_ReportsLineTwo()
    {
        using var engine = CreateEngine();

        // The bad token is on the second line
        var ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Execute("SELECT *\nFROQ users"));

        Assert.Matches(@"\[2:\d+\]", ex.Message);
    }

    [Fact]
    public void ErrorOnThirdLine_ReportsLineThree()
    {
        using var engine = CreateEngine();

        // '@' is an unexpected character on line 3
        var ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Execute("SELECT *\nFROM users\nWHERE @bad = 1"));

        Assert.Matches(@"\[3:\d+\]", ex.Message);
    }

    // ── Column accuracy ──────────────────────────────────────────────────────

    [Fact]
    public void ColumnNumber_ReflectsOffsetOnLine()
    {
        using var engine1 = CreateEngine();

        // "FROQ" starts at col 1 — unsupported statement token
        var ex1 = Assert.Throws<InvalidOperationException>(() =>
            engine1.Execute("FROQ users"));
        Assert.Contains("[1:1]", ex1.Message);

        using var engine2 = CreateEngine();

        // Error occurs somewhere on line 1, past col 1
        var ex2 = Assert.Throws<InvalidOperationException>(() =>
            engine2.Execute("SELECT FROQ"));
        Assert.Matches(@"\[1:\d+\]", ex2.Message);
        // The column should be greater than 1 since "SELECT " precedes the error
        var colMatch = System.Text.RegularExpressions.Regex.Match(ex2.Message, @"\[1:(\d+)\]");
        Assert.True(int.Parse(colMatch.Groups[1].Value) > 1);
    }

    // ── Tokenizer-level errors ───────────────────────────────────────────────

    [Fact]
    public void UnexpectedCharacter_IncludesPosition()
    {
        using var engine = CreateEngine();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Execute("SELECT @ FROM t"));

        Assert.Matches(@"\[1:\d+\]", ex.Message);
        Assert.Contains("@", ex.Message);
    }
}
