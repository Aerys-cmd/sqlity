using Sqlity.Cli;

namespace Sqlity.Cli.Tests;

public sealed class SqlityCliTests
{
    [Fact]
    public void Run_executes_sql_from_arguments_and_reopens_the_same_database_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            var createExitCode = Run(path, "CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);", out var createOutput, out var createError);
            var insertExitCode = Run(path, "INSERT INTO users VALUES (1, 'Ada', TRUE);", out var insertOutput, out var insertError);
            var selectExitCode = Run(path, "SELECT id, name FROM users WHERE id = 1;", out var selectOutput, out var selectError);

            Assert.Equal(0, createExitCode);
            Assert.Equal("Rows affected: 0", createOutput);
            Assert.Equal(string.Empty, createError);

            Assert.Equal(0, insertExitCode);
            Assert.Equal("Rows affected: 1", insertOutput);
            Assert.Equal(string.Empty, insertError);

            Assert.Equal(0, selectExitCode);
            Assert.Equal(
                """
                id | name
                1 | Ada
                (1 row(s))
                """.ReplaceLineEndings(),
                selectOutput.ReplaceLineEndings());
            Assert.Equal(string.Empty, selectError);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Run_accepts_sql_from_standard_input()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            Run(path, "CREATE TABLE files (id INT64 PRIMARY KEY, name STRING, payload BLOB);", out _, out _);
            Run(path, "INSERT INTO files VALUES (1, 'spec', X'CAFE');", out _, out _);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = SqlityCli.Run(
                [path],
                new StringReader("SELECT name, payload FROM files WHERE id = 1;"),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(
                """
                name | payload
                spec | X'CAFE'
                (1 row(s))
                """.ReplaceLineEndings(),
                stdout.ToString().ReplaceLineEndings().TrimEnd());
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Run_returns_usage_error_when_sql_is_missing()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = SqlityCli.Run(["demo.sqlity"], new StringReader(string.Empty), stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("No SQL text was provided.", stderr.ToString());
        Assert.Contains("Usage:", stderr.ToString());
    }

    [Fact]
    public void Executable_examples_match_the_sample_readme()
    {
        var expected = CliSampleReadme.Generate();

        if (CliSampleReadme.ShouldUpdateFromEnvironment())
        {
            File.WriteAllText(CliSampleReadme.FilePath, expected);
        }

        var actual = File.ReadAllText(CliSampleReadme.FilePath).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual);
    }

    private static int Run(string path, string sql, out string stdoutText, out string stderrText)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = SqlityCli.Run([path, sql], new StringReader(string.Empty), stdout, stderr);
        stdoutText = stdout.ToString().TrimEnd();
        stderrText = stderr.ToString().TrimEnd();
        return exitCode;
    }
}
