namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for Phase 3 set operations: UNION, UNION ALL, INTERSECT, EXCEPT.
/// </summary>
public sealed class Phase3SetOperationTests
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

    // ── UNION ALL ─────────────────────────────────────────────────────────────

    [Fact]
    public void UnionAll_combines_all_rows_including_duplicates()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO a VALUES (1, 'x')");
            engine.Execute("INSERT INTO a VALUES (2, 'y')");
            engine.Execute("INSERT INTO b VALUES (3, 'y')");
            engine.Execute("INSERT INTO b VALUES (4, 'z')");

            var result = engine.Execute("SELECT val FROM a UNION ALL SELECT val FROM b");

            Assert.Equal(4, result.Rows.Count);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void UnionAll_preserves_duplicate_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 10)");

            // Same row from both sides — UNION ALL keeps both.
            var result = engine.Execute("SELECT v FROM t UNION ALL SELECT v FROM t");

            Assert.Equal(2, result.Rows.Count);
            Assert.All(result.Rows, row => Assert.Equal(10L, row[0]));
        }
        finally { Cleanup(engine, path); }
    }

    // ── UNION (DISTINCT) ──────────────────────────────────────────────────────

    [Fact]
    public void Union_removes_duplicate_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO a VALUES (1, 'hello')");
            engine.Execute("INSERT INTO a VALUES (2, 'world')");
            engine.Execute("INSERT INTO b VALUES (3, 'world')");
            engine.Execute("INSERT INTO b VALUES (4, 'foo')");

            var result = engine.Execute("SELECT val FROM a UNION SELECT val FROM b");

            // 'hello', 'world', 'foo' — 'world' appears in both but deduped.
            Assert.Equal(3, result.Rows.Count);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Union_with_no_overlap_returns_all_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO a VALUES (1, 1)");
            engine.Execute("INSERT INTO a VALUES (2, 2)");
            engine.Execute("INSERT INTO b VALUES (3, 3)");
            engine.Execute("INSERT INTO b VALUES (4, 4)");

            var result = engine.Execute("SELECT n FROM a UNION SELECT n FROM b");

            Assert.Equal(4, result.Rows.Count);
        }
        finally { Cleanup(engine, path); }
    }

    // ── INTERSECT ─────────────────────────────────────────────────────────────

    [Fact]
    public void Intersect_returns_common_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO a VALUES (1, 1)");
            engine.Execute("INSERT INTO a VALUES (2, 2)");
            engine.Execute("INSERT INTO a VALUES (3, 3)");
            engine.Execute("INSERT INTO b VALUES (1, 2)");
            engine.Execute("INSERT INTO b VALUES (2, 3)");
            engine.Execute("INSERT INTO b VALUES (3, 4)");

            var result = engine.Execute("SELECT n FROM a INTERSECT SELECT n FROM b");

            Assert.Equal(2, result.Rows.Count);
            var vals = result.Rows.Select(r => (long)r[0]!).OrderBy(v => v).ToList();
            Assert.Equal([2L, 3L], vals);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Intersect_returns_empty_when_no_common_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO a VALUES (1, 1)");
            engine.Execute("INSERT INTO b VALUES (1, 2)");

            var result = engine.Execute("SELECT n FROM a INTERSECT SELECT n FROM b");

            Assert.Empty(result.Rows);
        }
        finally { Cleanup(engine, path); }
    }

    // ── EXCEPT ────────────────────────────────────────────────────────────────

    [Fact]
    public void Except_removes_right_side_rows_from_left()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO a VALUES (1, 1)");
            engine.Execute("INSERT INTO a VALUES (2, 2)");
            engine.Execute("INSERT INTO a VALUES (3, 3)");
            engine.Execute("INSERT INTO b VALUES (1, 2)");

            var result = engine.Execute("SELECT n FROM a EXCEPT SELECT n FROM b");

            // 2 is in b, so result should be 1 and 3.
            Assert.Equal(2, result.Rows.Count);
            var vals = result.Rows.Select(r => (long)r[0]!).OrderBy(v => v).ToList();
            Assert.Equal([1L, 3L], vals);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Except_returns_all_left_rows_when_right_is_empty()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO a VALUES (1, 10)");
            engine.Execute("INSERT INTO a VALUES (2, 20)");

            var result = engine.Execute("SELECT n FROM a EXCEPT SELECT n FROM b");

            Assert.Equal(2, result.Rows.Count);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Set ops with ORDER BY / LIMIT ─────────────────────────────────────────

    [Fact]
    public void UnionAll_with_order_by_sorts_combined_result()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO a VALUES (1, 3)");
            engine.Execute("INSERT INTO a VALUES (2, 1)");
            engine.Execute("INSERT INTO b VALUES (1, 2)");

            var result = engine.Execute("SELECT n FROM a UNION ALL SELECT n FROM b ORDER BY n");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal(2L, result.Rows[1][0]);
            Assert.Equal(3L, result.Rows[2][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void UnionAll_with_limit_truncates_combined_result()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO a VALUES (1, 1)");
            engine.Execute("INSERT INTO a VALUES (2, 2)");
            engine.Execute("INSERT INTO b VALUES (1, 3)");

            var result = engine.Execute("SELECT n FROM a UNION ALL SELECT n FROM b ORDER BY n LIMIT 2");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal(2L, result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Column mismatch ───────────────────────────────────────────────────────

    [Fact]
    public void Union_throws_when_column_counts_differ()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, x INT64, y INT64)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, x INT64)");
            engine.Execute("INSERT INTO a VALUES (1, 1, 2)");
            engine.Execute("INSERT INTO b VALUES (1, 1)");

            Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("SELECT x, y FROM a UNION SELECT x FROM b"));
        }
        finally { Cleanup(engine, path); }
    }
}
