using System.Text;
using Sqlity.Cli;

namespace Sqlity.Cli.Tests;

internal static class CliSampleReadme
{
    private const string UpdateExamplesEnvironmentVariable = "SQLITY_UPDATE_EXECUTABLE_EXAMPLES";

    public static string FilePath =>
        Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            "samples",
            "Sqlity.Cli",
            "README.md");

    public static string UpdateCommand =>
        $"{UpdateExamplesEnvironmentVariable}=1 dotnet test tests/Sqlity.Cli.Tests/Sqlity.Cli.Tests.csproj";

    public static bool ShouldUpdateFromEnvironment() =>
        string.Equals(
            Environment.GetEnvironmentVariable(UpdateExamplesEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    public static string Generate()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Sqlity.Cli");
        builder.AppendLine();
        builder.AppendLine("`Sqlity.Cli` is the tiny runnable sample for opening a `.sqlity` file and executing one SQL statement at a time.");
        builder.AppendLine();
        builder.AppendLine("These examples are generated from executable tests so the documented command output stays in sync with the sample.");
        builder.AppendLine();
        builder.AppendLine("To refresh this file:");
        builder.AppendLine();
        builder.AppendLine("```bash");
        builder.AppendLine(UpdateCommand);
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("Each command below reopens the same `demo.sqlity` file, so the workflow mirrors normal CLI usage.");
        builder.AppendLine();

        using var scenario = new ExampleScenario();
        scenario.AppendSection(
            builder,
            "Create a table",
            "dotnet run --project samples/Sqlity.Cli -- demo.sqlity \"CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);\"",
            ["demo.sqlity", "CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);"]);
        scenario.AppendSection(
            builder,
            "Insert a row",
            "dotnet run --project samples/Sqlity.Cli -- demo.sqlity \"INSERT INTO users VALUES (1, 'Ada', TRUE);\"",
            ["demo.sqlity", "INSERT INTO users VALUES (1, 'Ada', TRUE);"]);
        scenario.AppendSection(
            builder,
            "Insert with a reordered column list",
            "dotnet run --project samples/Sqlity.Cli -- demo.sqlity \"INSERT INTO users (is_active, name, id) VALUES (FALSE, 'Linus', 2);\"",
            ["demo.sqlity", "INSERT INTO users (is_active, name, id) VALUES (FALSE, 'Linus', 2);"]);
        scenario.AppendSection(
            builder,
            "Select a projection by primary key",
            "dotnet run --project samples/Sqlity.Cli -- demo.sqlity \"SELECT id, name FROM users WHERE id = 2;\"",
            ["demo.sqlity", "SELECT id, name FROM users WHERE id = 2;"]);
        scenario.AppendSection(
            builder,
            "Update a row",
            "dotnet run --project samples/Sqlity.Cli -- demo.sqlity \"UPDATE users SET name = 'Ada Lovelace' WHERE id = 1;\"",
            ["demo.sqlity", "UPDATE users SET name = 'Ada Lovelace' WHERE id = 1;"]);
        scenario.AppendSection(
            builder,
            "Delete a row",
            "dotnet run --project samples/Sqlity.Cli -- demo.sqlity \"DELETE FROM users WHERE id = 2;\"",
            ["demo.sqlity", "DELETE FROM users WHERE id = 2;"]);
        scenario.AppendSection(
            builder,
            "Create a table with a BLOB column",
            "dotnet run --project samples/Sqlity.Cli -- demo.sqlity \"CREATE TABLE files (id INT64 PRIMARY KEY, name STRING, payload BLOB);\"",
            ["demo.sqlity", "CREATE TABLE files (id INT64 PRIMARY KEY, name STRING, payload BLOB);"]);
        scenario.AppendSection(
            builder,
            "Insert a BLOB literal",
            "dotnet run --project samples/Sqlity.Cli -- demo.sqlity \"INSERT INTO files VALUES (1, 'spec', X'CAFE');\"",
            ["demo.sqlity", "INSERT INTO files VALUES (1, 'spec', X'CAFE');"]);
        scenario.AppendSection(
            builder,
            "Pipe SQL through standard input",
            "echo \"SELECT name, payload FROM files WHERE id = 1;\" | dotnet run --project samples/Sqlity.Cli -- demo.sqlity",
            ["demo.sqlity"],
            "SELECT name, payload FROM files WHERE id = 1;");

        return builder.ToString().ReplaceLineEndings("\n");
    }

    private sealed class ExampleScenario : IDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        public void AppendSection(
            StringBuilder builder,
            string title,
            string displayedCommand,
            string[] displayedArguments,
            string? stdin = null)
        {
            var output = Run(displayedArguments, stdin);

            builder.AppendLine($"## {title}");
            builder.AppendLine();
            builder.AppendLine("```bash");
            builder.AppendLine(displayedCommand);
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(output);
            builder.AppendLine("```");
            builder.AppendLine();
        }

        public void Dispose()
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }

        private string Run(string[] displayedArguments, string? stdin)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = SqlityCli.Run(MapDisplayedArguments(displayedArguments), new StringReader(stdin ?? string.Empty), stdout, stderr);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Example command failed: {string.Join(' ', displayedArguments)}{Environment.NewLine}{stderr}");
            }

            return stdout.ToString().TrimEnd();
        }

        private string[] MapDisplayedArguments(string[] displayedArguments) =>
            displayedArguments
                .Select(argument => string.Equals(argument, "demo.sqlity", StringComparison.Ordinal) ? _databasePath : argument)
                .ToArray();
    }
}
