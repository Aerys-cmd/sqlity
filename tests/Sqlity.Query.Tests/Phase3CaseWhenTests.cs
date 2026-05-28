namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for Phase 3 CASE WHEN expressions in SELECT and WHERE.
/// </summary>
public sealed class Phase3CaseWhenTests
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

    // ── SELECT: basic literal results ─────────────────────────────────────────

    [Fact]
    public void CaseWhen_in_select_returns_matched_literal()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 90)");

            var result = engine.Execute(
                "SELECT CASE WHEN score >= 90 THEN 'A' ELSE 'B' END FROM t WHERE id = 1");

            Assert.Single(result.Rows);
            Assert.Equal("A", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void CaseWhen_in_select_falls_through_to_else()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 50)");

            var result = engine.Execute(
                "SELECT CASE WHEN score >= 90 THEN 'A' ELSE 'B' END FROM t WHERE id = 1");

            Assert.Single(result.Rows);
            Assert.Equal("B", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void CaseWhen_without_else_returns_null_when_no_branch_matches()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 50)");

            var result = engine.Execute(
                "SELECT CASE WHEN score >= 90 THEN 'A' END FROM t WHERE id = 1");

            Assert.Single(result.Rows);
            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── SELECT: multiple WHEN branches (first-match semantics) ────────────────

    [Fact]
    public void CaseWhen_multiple_branches_picks_first_match()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 85)");
            engine.Execute("INSERT INTO t VALUES (2, 72)");
            engine.Execute("INSERT INTO t VALUES (3, 55)");

            var result = engine.Execute(
                "SELECT id, CASE WHEN score >= 90 THEN 'A' WHEN score >= 80 THEN 'B' WHEN score >= 70 THEN 'C' ELSE 'F' END AS grade FROM t ORDER BY id");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal("B", result.Rows[0][1]);
            Assert.Equal("C", result.Rows[1][1]);
            Assert.Equal("F", result.Rows[2][1]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── SELECT: alias support ─────────────────────────────────────────────────

    [Fact]
    public void CaseWhen_supports_column_alias()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, active INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 1)");

            var result = engine.Execute(
                "SELECT CASE WHEN active = 1 THEN 'yes' ELSE 'no' END AS is_active FROM t");

            Assert.Single(result.Rows);
            Assert.Equal("is_active", result.Columns[0]);
            Assert.Equal("yes", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── SELECT: column reference as THEN/ELSE result ──────────────────────────

    [Fact]
    public void CaseWhen_then_can_return_column_value()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, a TEXT, b TEXT, flag INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 'alpha', 'beta', 1)");
            engine.Execute("INSERT INTO t VALUES (2, 'alpha', 'beta', 0)");

            var result = engine.Execute(
                "SELECT CASE WHEN flag = 1 THEN a ELSE b END AS chosen FROM t ORDER BY id");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal("alpha", result.Rows[0][0]);
            Assert.Equal("beta", result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── WHERE: CASE WHEN … END op literal ─────────────────────────────────────

    [Fact]
    public void CaseWhen_in_where_filters_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, status TEXT)");
            engine.Execute("INSERT INTO t VALUES (1, 'active')");
            engine.Execute("INSERT INTO t VALUES (2, 'inactive')");
            engine.Execute("INSERT INTO t VALUES (3, 'active')");

            var result = engine.Execute(
                "SELECT id FROM t WHERE CASE WHEN status = 'active' THEN 1 ELSE 0 END = 1 ORDER BY id");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal(3L, result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void CaseWhen_in_where_no_match_no_else_excludes_row()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 50)");

            // No branch matches and no ELSE → NULL, which never equals 1
            var result = engine.Execute(
                "SELECT id FROM t WHERE CASE WHEN score >= 90 THEN 1 END = 1");

            Assert.Empty(result.Rows);
        }
        finally { Cleanup(engine, path); }
    }
}
