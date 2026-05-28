namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for Phase 3 scalar string and math functions:
/// UPPER, LOWER, TRIM, LENGTH, SUBSTR, REPLACE, ABS, ROUND, CEIL, FLOOR.
/// </summary>
public sealed class Phase3ScalarFunctionsTests
{
    private static (QueryEngine Engine, string Path) CreateEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        return (new QueryEngine(path), path);
    }

    private static void Cleanup(QueryEngine engine, string path)
    {
        engine.Dispose();
        if (File.Exists(path)) File.Delete(path);
    }

    // ── UPPER ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Upper_returns_uppercase_string()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, 'hello')");

            var result = engine.Execute("SELECT UPPER(name) FROM t WHERE id = 1");

            Assert.Single(result.Rows);
            Assert.Equal("HELLO", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Upper_returns_null_for_null_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT UPPER(name) FROM t WHERE id = 1");

            Assert.Single(result.Rows);
            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Upper_supports_alias()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, 'world')");

            var result = engine.Execute("SELECT UPPER(name) AS up FROM t WHERE id = 1");

            Assert.Equal("up", result.Columns[0], ignoreCase: true);
            Assert.Equal("WORLD", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── LOWER ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Lower_returns_lowercase_string()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, 'HELLO')");

            var result = engine.Execute("SELECT LOWER(name) FROM t WHERE id = 1");

            Assert.Equal("hello", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Lower_returns_null_for_null_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT LOWER(name) FROM t WHERE id = 1");

            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── TRIM ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Trim_removes_leading_and_trailing_whitespace()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, '  hello  ')");

            var result = engine.Execute("SELECT TRIM(val) FROM t WHERE id = 1");

            Assert.Equal("hello", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Trim_returns_null_for_null_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT TRIM(val) FROM t WHERE id = 1");

            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── LENGTH ────────────────────────────────────────────────────────────────

    [Fact]
    public void Length_returns_character_count()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, 'hello')");

            var result = engine.Execute("SELECT LENGTH(val) FROM t WHERE id = 1");

            Assert.Equal(5L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Length_returns_zero_for_empty_string()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, '')");

            var result = engine.Execute("SELECT LENGTH(val) FROM t WHERE id = 1");

            Assert.Equal(0L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Length_returns_null_for_null_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT LENGTH(val) FROM t WHERE id = 1");

            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── SUBSTR ────────────────────────────────────────────────────────────────

    [Fact]
    public void Substr_two_args_returns_suffix_from_1based_position()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, 'hello')");

            var result = engine.Execute("SELECT SUBSTR(val, 2) FROM t WHERE id = 1");

            Assert.Equal("ello", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Substr_three_args_returns_substring_of_given_length()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, 'hello world')");

            var result = engine.Execute("SELECT SUBSTR(val, 1, 5) FROM t WHERE id = 1");

            Assert.Equal("hello", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Substr_returns_null_for_null_string()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT SUBSTR(val, 1) FROM t WHERE id = 1");

            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Substr_returns_empty_when_start_beyond_end()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, 'hi')");

            var result = engine.Execute("SELECT SUBSTR(val, 10) FROM t WHERE id = 1");

            Assert.Equal(string.Empty, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── REPLACE ───────────────────────────────────────────────────────────────

    [Fact]
    public void Replace_substitutes_occurrences()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, 'foo bar foo')");

            var result = engine.Execute("SELECT REPLACE(val, 'foo', 'baz') FROM t WHERE id = 1");

            Assert.Equal("baz bar baz", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Replace_returns_null_when_subject_is_null()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT REPLACE(val, 'a', 'b') FROM t WHERE id = 1");

            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── ABS ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Abs_returns_absolute_value_for_negative_integer()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO t VALUES (1, -42)");

            var result = engine.Execute("SELECT ABS(val) FROM t WHERE id = 1");

            Assert.Equal(42L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Abs_returns_absolute_value_for_negative_float()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val REAL)");
            engine.Execute("INSERT INTO t VALUES (1, -3.14)");

            var result = engine.Execute("SELECT ABS(val) FROM t WHERE id = 1");

            Assert.Equal(3.14, (double)result.Rows[0][0]!, 5);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Abs_returns_null_for_null_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT ABS(val) FROM t WHERE id = 1");

            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── ROUND ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Round_one_arg_rounds_to_nearest_integer()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val REAL)");
            engine.Execute("INSERT INTO t VALUES (1, 2.7)");

            var result = engine.Execute("SELECT ROUND(val) FROM t WHERE id = 1");

            Assert.Equal(3.0, (double)result.Rows[0][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Round_two_args_rounds_to_specified_decimal_places()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val REAL)");
            engine.Execute("INSERT INTO t VALUES (1, 3.14159)");

            var result = engine.Execute("SELECT ROUND(val, 2) FROM t WHERE id = 1");

            Assert.Equal(3.14, (double)result.Rows[0][0]!, 5);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Round_returns_null_for_null_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val REAL)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT ROUND(val) FROM t WHERE id = 1");

            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── CEIL ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Ceil_rounds_up_to_nearest_integer()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val REAL)");
            engine.Execute("INSERT INTO t VALUES (1, 2.1)");

            var result = engine.Execute("SELECT CEIL(val) FROM t WHERE id = 1");

            Assert.Equal(3.0, (double)result.Rows[0][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Ceil_returns_integer_unchanged_for_integer_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 5)");

            var result = engine.Execute("SELECT CEIL(val) FROM t WHERE id = 1");

            Assert.Equal(5L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Ceil_returns_null_for_null_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val REAL)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT CEIL(val) FROM t WHERE id = 1");

            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── FLOOR ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Floor_rounds_down_to_nearest_integer()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val REAL)");
            engine.Execute("INSERT INTO t VALUES (1, 2.9)");

            var result = engine.Execute("SELECT FLOOR(val) FROM t WHERE id = 1");

            Assert.Equal(2.0, (double)result.Rows[0][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Floor_returns_integer_unchanged_for_integer_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 7)");

            var result = engine.Execute("SELECT FLOOR(val) FROM t WHERE id = 1");

            Assert.Equal(7L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Floor_returns_null_for_null_input()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val REAL)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");

            var result = engine.Execute("SELECT FLOOR(val) FROM t WHERE id = 1");

            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── REPLACE as scalar function vs INSERT OR REPLACE disambiguation ────────

    [Fact]
    public void Replace_scalar_function_does_not_conflict_with_insert_or_replace()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT OR REPLACE INTO t VALUES (1, 'hello')");
            engine.Execute("INSERT OR REPLACE INTO t VALUES (1, 'world')");

            var result = engine.Execute("SELECT REPLACE(val, 'world', 'earth') FROM t WHERE id = 1");

            Assert.Equal("earth", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Arity validation ──────────────────────────────────────────────────────

    [Fact]
    public void Upper_with_wrong_arity_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            Assert.Throws<InvalidOperationException>(
                () => engine.Execute("SELECT UPPER(val, val) FROM t"));
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Replace_with_wrong_arity_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            Assert.Throws<InvalidOperationException>(
                () => engine.Execute("SELECT REPLACE(val, 'a') FROM t"));
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Substr_with_one_arg_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT)");
            Assert.Throws<InvalidOperationException>(
                () => engine.Execute("SELECT SUBSTR(val) FROM t"));
        }
        finally { Cleanup(engine, path); }
    }
}
