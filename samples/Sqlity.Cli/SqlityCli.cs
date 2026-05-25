using System.Globalization;
using Sqlity.Query;

namespace Sqlity.Cli;

public static class SqlityCli
{
    private const string UsageText = """
        Usage:
          Sqlity.Cli <database-path> "<sql>"
          <sql> | Sqlity.Cli <database-path>
        """;

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (!TryParseInvocation(args, stdin, stderr, out var invocation))
        {
            return 1;
        }

        try
        {
            using var engine = new QueryEngine(invocation.DatabasePath);
            var result = engine.Execute(invocation.Sql);
            WriteResult(stdout, result);
            return 0;
        }
        catch (ArgumentException exception)
        {
            stderr.WriteLine(exception.Message);
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            stderr.WriteLine(exception.Message);
            return 1;
        }
        catch (IOException exception)
        {
            stderr.WriteLine(exception.Message);
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            stderr.WriteLine(exception.Message);
            return 1;
        }
    }

    private static bool TryParseInvocation(string[] args, TextReader stdin, TextWriter stderr, out Invocation invocation)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine(UsageText);
            invocation = default;
            return false;
        }

        var databasePath = args[0];
        var sql = args.Length > 1
            ? string.Join(' ', args.Skip(1))
            : stdin.ReadToEnd();

        if (string.IsNullOrWhiteSpace(sql))
        {
            stderr.WriteLine("No SQL text was provided.");
            stderr.WriteLine(UsageText);
            invocation = default;
            return false;
        }

        invocation = new Invocation(databasePath, sql.Trim());
        return true;
    }

    private static void WriteResult(TextWriter stdout, QueryExecutionResult result)
    {
        if (result.Columns.Count == 0)
        {
            stdout.WriteLine($"Rows affected: {result.RowsAffected}");
            return;
        }

        stdout.WriteLine(string.Join(" | ", result.Columns));

        foreach (var row in result.Rows)
        {
            stdout.WriteLine(string.Join(" | ", row.Select(FormatValue)));
        }

        stdout.WriteLine($"({result.Rows.Count} row(s))");
    }

    private static string FormatValue(object? value) =>
        value switch
        {
            null => "NULL",
            bool boolean => boolean ? "TRUE" : "FALSE",
            byte[] blob => $"X'{Convert.ToHexString(blob)}'",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    private readonly record struct Invocation(string DatabasePath, string Sql);
}
