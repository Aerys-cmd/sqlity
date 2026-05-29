namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for Phase 3 window functions:
/// ROW_NUMBER, RANK, DENSE_RANK, LAG, LEAD with OVER (PARTITION BY … ORDER BY …).
/// </summary>
public sealed class Phase3WindowFunctionTests
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

    // ── ROW_NUMBER ────────────────────────────────────────────────────────────

    [Fact]
    public void RowNumber_assigns_sequential_numbers_within_partition()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE scores (id INT64 PRIMARY KEY, dept TEXT, score INT64)");
            engine.Execute("INSERT INTO scores VALUES (1, 'eng', 90)");
            engine.Execute("INSERT INTO scores VALUES (2, 'eng', 70)");
            engine.Execute("INSERT INTO scores VALUES (3, 'hr', 80)");
            engine.Execute("INSERT INTO scores VALUES (4, 'hr', 60)");

            var result = engine.Execute(
                "SELECT dept, score, ROW_NUMBER() OVER (PARTITION BY dept ORDER BY score DESC) AS rn " +
                "FROM scores ORDER BY dept, score DESC");

            Assert.Equal(4, result.Rows.Count);
            // eng partition: score 90 → rn=1, score 70 → rn=2
            var engRows = result.Rows.Where(r => (string)r[0]! == "eng").OrderByDescending(r => (long)r[1]!).ToList();
            Assert.Equal(1L, engRows[0][2]);
            Assert.Equal(2L, engRows[1][2]);

            // hr partition: score 80 → rn=1, score 60 → rn=2
            var hrRows = result.Rows.Where(r => (string)r[0]! == "hr").OrderByDescending(r => (long)r[1]!).ToList();
            Assert.Equal(1L, hrRows[0][2]);
            Assert.Equal(2L, hrRows[1][2]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void RowNumber_without_partition_numbers_entire_result_set()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 30)");
            engine.Execute("INSERT INTO t VALUES (2, 10)");
            engine.Execute("INSERT INTO t VALUES (3, 20)");

            var result = engine.Execute(
                "SELECT v, ROW_NUMBER() OVER (ORDER BY v) AS rn FROM t ORDER BY v");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][1]);
            Assert.Equal(2L, result.Rows[1][1]);
            Assert.Equal(3L, result.Rows[2][1]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── RANK ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Rank_assigns_same_rank_to_tied_rows_and_skips_numbers()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 100)");
            engine.Execute("INSERT INTO t VALUES (2, 90)");
            engine.Execute("INSERT INTO t VALUES (3, 90)");
            engine.Execute("INSERT INTO t VALUES (4, 80)");

            var result = engine.Execute(
                "SELECT score, RANK() OVER (ORDER BY score DESC) AS rnk FROM t ORDER BY score DESC");

            Assert.Equal(4, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][1]); // 100 → rank 1
            Assert.Equal(2L, result.Rows[1][1]); // 90 → rank 2
            Assert.Equal(2L, result.Rows[2][1]); // 90 → rank 2 (tie)
            Assert.Equal(4L, result.Rows[3][1]); // 80 → rank 4 (gap after tie)
        }
        finally { Cleanup(engine, path); }
    }

    // ── DENSE_RANK ────────────────────────────────────────────────────────────

    [Fact]
    public void DenseRank_assigns_same_rank_to_tied_rows_without_gaps()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 100)");
            engine.Execute("INSERT INTO t VALUES (2, 90)");
            engine.Execute("INSERT INTO t VALUES (3, 90)");
            engine.Execute("INSERT INTO t VALUES (4, 80)");

            var result = engine.Execute(
                "SELECT score, DENSE_RANK() OVER (ORDER BY score DESC) AS dr FROM t ORDER BY score DESC");

            Assert.Equal(4, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][1]); // 100 → rank 1
            Assert.Equal(2L, result.Rows[1][1]); // 90 → rank 2
            Assert.Equal(2L, result.Rows[2][1]); // 90 → rank 2 (tie)
            Assert.Equal(3L, result.Rows[3][1]); // 80 → rank 3 (no gap)
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Rank_and_DenseRank_differ_when_there_are_ties()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 5)");
            engine.Execute("INSERT INTO t VALUES (2, 5)");
            engine.Execute("INSERT INTO t VALUES (3, 3)");

            var rankResult = engine.Execute(
                "SELECT v, RANK() OVER (ORDER BY v DESC) AS rnk FROM t ORDER BY v DESC, id");
            var denseResult = engine.Execute(
                "SELECT v, DENSE_RANK() OVER (ORDER BY v DESC) AS dr FROM t ORDER BY v DESC, id");

            // RANK: 1,1,3
            Assert.Equal(1L, rankResult.Rows[0][1]);
            Assert.Equal(1L, rankResult.Rows[1][1]);
            Assert.Equal(3L, rankResult.Rows[2][1]);

            // DENSE_RANK: 1,1,2
            Assert.Equal(1L, denseResult.Rows[0][1]);
            Assert.Equal(1L, denseResult.Rows[1][1]);
            Assert.Equal(2L, denseResult.Rows[2][1]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── LAG ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Lag_returns_previous_row_value_within_partition()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, month INT64, revenue INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 1, 100)");
            engine.Execute("INSERT INTO t VALUES (2, 2, 120)");
            engine.Execute("INSERT INTO t VALUES (3, 3, 110)");

            var result = engine.Execute(
                "SELECT month, revenue, LAG(revenue) OVER (ORDER BY month) AS prev " +
                "FROM t ORDER BY month");

            Assert.Equal(3, result.Rows.Count);
            Assert.Null(result.Rows[0][2]);       // first row has no previous
            Assert.Equal(100L, result.Rows[1][2]); // month 2 prev = month 1
            Assert.Equal(120L, result.Rows[2][2]); // month 3 prev = month 2
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Lag_with_offset_2_returns_value_two_rows_before()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 10)");
            engine.Execute("INSERT INTO t VALUES (2, 20)");
            engine.Execute("INSERT INTO t VALUES (3, 30)");
            engine.Execute("INSERT INTO t VALUES (4, 40)");

            var result = engine.Execute(
                "SELECT n, LAG(n, 2) OVER (ORDER BY id) AS prev2 FROM t ORDER BY id");

            Assert.Equal(4, result.Rows.Count);
            Assert.Null(result.Rows[0][1]);        // id=1 offset 2: out of bounds
            Assert.Null(result.Rows[1][1]);        // id=2 offset 2: out of bounds
            Assert.Equal(10L, result.Rows[2][1]);  // id=3 offset 2: n=10
            Assert.Equal(20L, result.Rows[3][1]);  // id=4 offset 2: n=20
        }
        finally { Cleanup(engine, path); }
    }

    // ── LEAD ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Lead_returns_next_row_value_within_partition()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 10)");
            engine.Execute("INSERT INTO t VALUES (2, 20)");
            engine.Execute("INSERT INTO t VALUES (3, 30)");

            var result = engine.Execute(
                "SELECT n, LEAD(n) OVER (ORDER BY id) AS nxt FROM t ORDER BY id");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(20L, result.Rows[0][1]); // id=1 next = 20
            Assert.Equal(30L, result.Rows[1][1]); // id=2 next = 30
            Assert.Null(result.Rows[2][1]);        // id=3 has no next
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Lead_with_offset_returns_correct_forward_value()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, n INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 10)");
            engine.Execute("INSERT INTO t VALUES (2, 20)");
            engine.Execute("INSERT INTO t VALUES (3, 30)");
            engine.Execute("INSERT INTO t VALUES (4, 40)");

            var result = engine.Execute(
                "SELECT n, LEAD(n, 2) OVER (ORDER BY id) AS nxt2 FROM t ORDER BY id");

            Assert.Equal(4, result.Rows.Count);
            Assert.Equal(30L, result.Rows[0][1]); // 10 + 2 → 30
            Assert.Equal(40L, result.Rows[1][1]); // 20 + 2 → 40
            Assert.Null(result.Rows[2][1]);
            Assert.Null(result.Rows[3][1]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Partitioned LAG / LEAD ────────────────────────────────────────────────

    [Fact]
    public void Lag_respects_partition_boundary()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, grp TEXT, v INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 'a', 10)");
            engine.Execute("INSERT INTO t VALUES (2, 'a', 20)");
            engine.Execute("INSERT INTO t VALUES (3, 'b', 30)");
            engine.Execute("INSERT INTO t VALUES (4, 'b', 40)");

            var result = engine.Execute(
                "SELECT grp, v, LAG(v) OVER (PARTITION BY grp ORDER BY id) AS prev " +
                "FROM t ORDER BY grp, id");

            // grp 'a': id=1 prev=null, id=2 prev=10
            // grp 'b': id=3 prev=null, id=4 prev=30
            Assert.Null(result.Rows[0][2]);
            Assert.Equal(10L, result.Rows[1][2]);
            Assert.Null(result.Rows[2][2]);
            Assert.Equal(30L, result.Rows[3][2]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Multiple window functions in same SELECT ───────────────────────────────

    [Fact]
    public void Multiple_window_functions_in_same_select()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 100)");
            engine.Execute("INSERT INTO t VALUES (2, 90)");
            engine.Execute("INSERT INTO t VALUES (3, 90)");

            var result = engine.Execute(
                "SELECT score, " +
                "ROW_NUMBER() OVER (ORDER BY score DESC) AS rn, " +
                "RANK() OVER (ORDER BY score DESC) AS rnk, " +
                "DENSE_RANK() OVER (ORDER BY score DESC) AS dr " +
                "FROM t ORDER BY score DESC, id");

            Assert.Equal(3, result.Rows.Count);
            // Row 0: score=100, rn=1, rnk=1, dr=1
            Assert.Equal(1L, result.Rows[0][1]);
            Assert.Equal(1L, result.Rows[0][2]);
            Assert.Equal(1L, result.Rows[0][3]);
            // Rows 1&2: score=90, rn=2 or 3 (unique), rnk=2, dr=2
            Assert.Equal(2L, result.Rows[1][2]);
            Assert.Equal(2L, result.Rows[2][2]);
            Assert.Equal(2L, result.Rows[1][3]);
            Assert.Equal(2L, result.Rows[2][3]);
        }
        finally { Cleanup(engine, path); }
    }
}
