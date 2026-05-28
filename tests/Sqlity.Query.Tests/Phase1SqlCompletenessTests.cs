namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for Phase 1 SQL completeness:
/// LIKE/ILIKE, BETWEEN/NOT BETWEEN, SELECT DISTINCT, NOT IN,
/// UPDATE/DELETE without WHERE, multi-row INSERT, column aliases,
/// and COALESCE/NULLIF/IFNULL scalar functions.
/// </summary>
public sealed class Phase1SqlCompletenessTests
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

    // ── LIKE / ILIKE ──────────────────────────────────────────────────────────

    [Fact]
    public void Like_prefix_wildcard_matches_correct_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO users VALUES (2, 'Bob')");
            engine.Execute("INSERT INTO users VALUES (3, 'Alicia')");

            var result = engine.Execute("SELECT id FROM users WHERE name LIKE 'Ali%'");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToArray();
            Assert.Equal([1L, 3L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Like_single_char_wildcard_matches_correct_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE items (id INT64 PRIMARY KEY, code TEXT)");
            engine.Execute("INSERT INTO items VALUES (1, 'A1')");
            engine.Execute("INSERT INTO items VALUES (2, 'AB')");
            engine.Execute("INSERT INTO items VALUES (3, 'A')");

            var result = engine.Execute("SELECT id FROM items WHERE code LIKE 'A_'");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToArray();
            Assert.Equal([1L, 2L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Like_no_wildcard_acts_as_exact_match()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE tags (id INT64 PRIMARY KEY, val TEXT)");
            engine.Execute("INSERT INTO tags VALUES (1, 'hello')");
            engine.Execute("INSERT INTO tags VALUES (2, 'world')");

            var result = engine.Execute("SELECT id FROM tags WHERE val LIKE 'hello'");

            Assert.Single(result.Rows);
            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void ILike_is_case_insensitive()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO users VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO users VALUES (2, 'ALICE')");
            engine.Execute("INSERT INTO users VALUES (3, 'Bob')");

            var result = engine.Execute("SELECT id FROM users WHERE name ILIKE 'alice'");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToArray();
            Assert.Equal([1L, 2L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Not_Like_excludes_matching_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE words (id INT64 PRIMARY KEY, word TEXT)");
            engine.Execute("INSERT INTO words VALUES (1, 'apple')");
            engine.Execute("INSERT INTO words VALUES (2, 'apricot')");
            engine.Execute("INSERT INTO words VALUES (3, 'banana')");

            var result = engine.Execute("SELECT id FROM words WHERE word NOT LIKE 'ap%'");

            Assert.Single(result.Rows);
            Assert.Equal(3L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── BETWEEN / NOT BETWEEN ─────────────────────────────────────────────────

    [Fact]
    public void Between_integer_range_includes_bounds()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE scores (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO scores VALUES (1, 10)");
            engine.Execute("INSERT INTO scores VALUES (2, 20)");
            engine.Execute("INSERT INTO scores VALUES (3, 30)");
            engine.Execute("INSERT INTO scores VALUES (4, 40)");

            var result = engine.Execute("SELECT id FROM scores WHERE score BETWEEN 20 AND 30");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToArray();
            Assert.Equal([2L, 3L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Not_Between_excludes_range()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE scores (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO scores VALUES (1, 10)");
            engine.Execute("INSERT INTO scores VALUES (2, 20)");
            engine.Execute("INSERT INTO scores VALUES (3, 30)");
            engine.Execute("INSERT INTO scores VALUES (4, 40)");

            var result = engine.Execute("SELECT id FROM scores WHERE score NOT BETWEEN 20 AND 30");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToArray();
            Assert.Equal([1L, 4L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    // ── SELECT DISTINCT ───────────────────────────────────────────────────────

    [Fact]
    public void Select_distinct_removes_duplicate_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE entries (id INT64 PRIMARY KEY, cat TEXT)");
            engine.Execute("INSERT INTO entries VALUES (1, 'A')");
            engine.Execute("INSERT INTO entries VALUES (2, 'B')");
            engine.Execute("INSERT INTO entries VALUES (3, 'A')");
            engine.Execute("INSERT INTO entries VALUES (4, 'C')");

            var result = engine.Execute("SELECT DISTINCT cat FROM entries");

            Assert.Equal(3, result.Rows.Count);
            var vals = result.Rows.Select(r => (string)r[0]!).OrderBy(x => x).ToArray();
            Assert.Equal(["A", "B", "C"], vals);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Select_distinct_with_limit_applies_limit_after_dedup()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE data (id INT64 PRIMARY KEY, val INT64)");
            for (int i = 1; i <= 6; i++)
                engine.Execute($"INSERT INTO data VALUES ({i}, {(i % 3) + 1})");

            var result = engine.Execute("SELECT DISTINCT val FROM data LIMIT 2");

            Assert.Equal(2, result.Rows.Count);
        }
        finally { Cleanup(engine, path); }
    }

    // ── NOT IN ────────────────────────────────────────────────────────────────

    [Fact]
    public void Not_in_values_subquery_excludes_matching_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("CREATE TABLE excluded (id INT64 PRIMARY KEY, pid INT64)");
            engine.Execute("INSERT INTO products VALUES (1, 'Alpha')");
            engine.Execute("INSERT INTO products VALUES (2, 'Beta')");
            engine.Execute("INSERT INTO products VALUES (3, 'Gamma')");
            engine.Execute("INSERT INTO excluded VALUES (1, 2)");

            var result = engine.Execute("SELECT id FROM products WHERE id NOT IN (SELECT pid FROM excluded)");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToArray();
            Assert.Equal([1L, 3L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Not_in_with_null_in_list_returns_no_rows()
    {
        // SQL three-valued logic: col NOT IN (list with NULL) → UNKNOWN → false
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64 NULL)");
            engine.Execute("CREATE TABLE nulls (id INT64 PRIMARY KEY, n INT64 NULL)");
            engine.Execute("INSERT INTO t VALUES (1, 10)");
            engine.Execute("INSERT INTO t VALUES (2, 20)");
            engine.Execute("INSERT INTO nulls VALUES (1, NULL)");

            // When subquery returns a NULL, NOT IN should return no rows
            var result = engine.Execute("SELECT id FROM t WHERE val NOT IN (SELECT n FROM nulls)");

            Assert.Empty(result.Rows);
        }
        finally { Cleanup(engine, path); }
    }

    // ── UPDATE / DELETE WITHOUT WHERE ─────────────────────────────────────────

    [Fact]
    public void Delete_without_where_removes_all_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE logs (id INT64 PRIMARY KEY, msg TEXT)");
            engine.Execute("INSERT INTO logs VALUES (1, 'a')");
            engine.Execute("INSERT INTO logs VALUES (2, 'b')");
            engine.Execute("INSERT INTO logs VALUES (3, 'c')");

            var del = engine.Execute("DELETE FROM logs");
            Assert.Equal(3, del.RowsAffected);

            var sel = engine.Execute("SELECT id FROM logs");
            Assert.Empty(sel.Rows);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Update_without_where_updates_all_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE counters (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO counters VALUES (1, 10)");
            engine.Execute("INSERT INTO counters VALUES (2, 20)");
            engine.Execute("INSERT INTO counters VALUES (3, 30)");

            var upd = engine.Execute("UPDATE counters SET val = 0");
            Assert.Equal(3, upd.RowsAffected);

            var sel = engine.Execute("SELECT val FROM counters");
            Assert.All(sel.Rows, row => Assert.Equal(0L, row[0]));
        }
        finally { Cleanup(engine, path); }
    }

    // ── MULTI-ROW INSERT ──────────────────────────────────────────────────────

    [Fact]
    public void Multi_row_insert_inserts_all_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE colors (id INT64 PRIMARY KEY, name TEXT)");

            var result = engine.Execute("INSERT INTO colors VALUES (1, 'red'), (2, 'green'), (3, 'blue')");

            Assert.Equal(3, result.RowsAffected);

            var sel = engine.Execute("SELECT id FROM colors ORDER BY id");
            Assert.Equal(3, sel.Rows.Count);
            Assert.Equal(1L, sel.Rows[0][0]);
            Assert.Equal(2L, sel.Rows[1][0]);
            Assert.Equal(3L, sel.Rows[2][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Multi_row_insert_with_column_list_works()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE pts (id INT64 PRIMARY KEY, x INT64, y INT64)");

            var result = engine.Execute("INSERT INTO pts (id, x, y) VALUES (1, 10, 20), (2, 30, 40)");

            Assert.Equal(2, result.RowsAffected);

            var sel = engine.Execute("SELECT x, y FROM pts ORDER BY id");
            Assert.Equal(10L, sel.Rows[0][0]);
            Assert.Equal(20L, sel.Rows[0][1]);
            Assert.Equal(30L, sel.Rows[1][0]);
            Assert.Equal(40L, sel.Rows[1][1]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── COLUMN ALIASES ────────────────────────────────────────────────────────

    [Fact]
    public void Column_alias_is_used_as_output_name()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO users VALUES (1, 'Alice')");

            var result = engine.Execute("SELECT id AS user_id, name AS full_name FROM users");

            Assert.Equal("user_id", result.Columns[0]);
            Assert.Equal("full_name", result.Columns[1]);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal("Alice", result.Rows[0][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Aggregate_alias_is_used_as_output_name()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE items (id INT64 PRIMARY KEY, price INT64)");
            engine.Execute("INSERT INTO items VALUES (1, 10)");
            engine.Execute("INSERT INTO items VALUES (2, 20)");

            var result = engine.Execute("SELECT COUNT(*) AS total_count, SUM(price) AS total_price FROM items");

            Assert.Equal("total_count", result.Columns[0]);
            Assert.Equal("total_price", result.Columns[1]);
            Assert.Equal(2L, result.Rows[0][0]);
            Assert.Equal(30L, result.Rows[0][1]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── COALESCE / NULLIF / IFNULL ────────────────────────────────────────────

    [Fact]
    public void Coalesce_returns_first_non_null_column()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, a TEXT NULL, b TEXT NULL)");
            engine.Execute("INSERT INTO t VALUES (1, NULL, 'fallback')");
            engine.Execute("INSERT INTO t VALUES (2, 'primary', 'fallback')");
            engine.Execute("INSERT INTO t VALUES (3, NULL, NULL)");

            var result = engine.Execute("SELECT COALESCE(a, b) AS val FROM t ORDER BY id");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal("fallback", result.Rows[0][0]);
            Assert.Equal("primary", result.Rows[1][0]);
            Assert.Null(result.Rows[2][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Coalesce_with_literal_fallback()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val TEXT NULL)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");
            engine.Execute("INSERT INTO t VALUES (2, 'exists')");

            var result = engine.Execute("SELECT COALESCE(val, 'default') AS v FROM t ORDER BY id");

            Assert.Equal("default", result.Rows[0][0]);
            Assert.Equal("exists", result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Nullif_returns_null_when_values_are_equal()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 0)");
            engine.Execute("INSERT INTO t VALUES (2, 5)");

            var result = engine.Execute("SELECT NULLIF(val, 0) AS v FROM t ORDER BY id");

            Assert.Null(result.Rows[0][0]);
            Assert.Equal(5L, result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Ifnull_returns_fallback_when_column_is_null()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64 NULL)");
            engine.Execute("INSERT INTO t VALUES (1, NULL)");
            engine.Execute("INSERT INTO t VALUES (2, 42)");

            var result = engine.Execute("SELECT IFNULL(val, -1) AS v FROM t ORDER BY id");

            Assert.Equal(-1L, result.Rows[0][0]);
            Assert.Equal(42L, result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }
}
