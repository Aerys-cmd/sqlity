namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for additional SQL types: REAL/FLOAT and DATE/DATETIME.
/// </summary>
public sealed class AdditionalTypesTests
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

    // ── REAL / FLOAT ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("REAL")]
    [InlineData("FLOAT")]
    [InlineData("FLOAT64")]
    [InlineData("DOUBLE")]
    public void Float_column_accepts_decimal_literal_and_round_trips(string typeName)
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute($"CREATE TABLE t (id INT64 PRIMARY KEY, v {typeName})");
            engine.Execute("INSERT INTO t VALUES (1, 3.14)");

            var result = engine.Execute("SELECT v FROM t WHERE id = 1");

            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(3.14, (double)result.Rows[0][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Float_column_accepts_integer_literal_and_coerces()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v REAL)");
            engine.Execute("INSERT INTO t VALUES (1, 42)");

            var result = engine.Execute("SELECT v FROM t WHERE id = 1");

            Assert.Equal(42.0, (double)result.Rows[0][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Float_column_accepts_negative_literal()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v REAL)");
            engine.Execute("INSERT INTO t VALUES (1, -2.5)");

            var result = engine.Execute("SELECT v FROM t WHERE id = 1");

            Assert.Equal(-2.5, (double)result.Rows[0][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Float_column_comparison_with_decimal_literal()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE prices (id INT64 PRIMARY KEY, amount REAL)");
            engine.Execute("INSERT INTO prices VALUES (1, 9.99)");
            engine.Execute("INSERT INTO prices VALUES (2, 19.99)");
            engine.Execute("INSERT INTO prices VALUES (3, 4.99)");

            var result = engine.Execute("SELECT id FROM prices WHERE amount < 10.0");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToList();
            Assert.Equal([1L, 3L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Float_column_comparison_with_integer_literal()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v REAL)");
            engine.Execute("INSERT INTO t VALUES (1, 5.0)");
            engine.Execute("INSERT INTO t VALUES (2, 15.0)");

            var result = engine.Execute("SELECT id FROM t WHERE v > 10");

            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(2L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Float_column_order_by()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v REAL)");
            engine.Execute("INSERT INTO t VALUES (1, 3.0)");
            engine.Execute("INSERT INTO t VALUES (2, 1.5)");
            engine.Execute("INSERT INTO t VALUES (3, -0.5)");

            var result = engine.Execute("SELECT v FROM t ORDER BY v ASC");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(-0.5, (double)result.Rows[0][0]!);
            Assert.Equal(1.5, (double)result.Rows[1][0]!);
            Assert.Equal(3.0, (double)result.Rows[2][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Float_column_avg_returns_double()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v REAL)");
            engine.Execute("INSERT INTO t VALUES (1, 1.0)");
            engine.Execute("INSERT INTO t VALUES (2, 2.0)");
            engine.Execute("INSERT INTO t VALUES (3, 3.0)");

            var result = engine.Execute("SELECT AVG(v) FROM t");

            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(2.0, (double)result.Rows[0][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Float_column_sum_returns_double()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v REAL)");
            engine.Execute("INSERT INTO t VALUES (1, 1.5)");
            engine.Execute("INSERT INTO t VALUES (2, 2.5)");

            var result = engine.Execute("SELECT SUM(v) FROM t");

            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(4.0, (double)result.Rows[0][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Float_column_index_seek()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score REAL)");
            engine.Execute("CREATE INDEX idx_score ON t (score)");
            engine.Execute("INSERT INTO t VALUES (1, 9.5)");
            engine.Execute("INSERT INTO t VALUES (2, 7.0)");
            engine.Execute("INSERT INTO t VALUES (3, 9.5)");

            var result = engine.Execute("SELECT id FROM t WHERE score = 9.5");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToList();
            Assert.Equal([1L, 3L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Float_database_persists_and_reopens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var engine = new QueryEngine(path))
            {
                engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v REAL)");
                engine.Execute("INSERT INTO t VALUES (1, 2.71828)");
            }

            using var reopened = new QueryEngine(path);
            var result = reopened.Execute("SELECT v FROM t WHERE id = 1");

            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(2.71828, (double)result.Rows[0][0]!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── DATE ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Date_column_accepts_iso8601_string_and_round_trips()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE events (id INT64 PRIMARY KEY, on_date DATE)");
            engine.Execute("INSERT INTO events VALUES (1, '2024-03-15')");

            var result = engine.Execute("SELECT on_date FROM events WHERE id = 1");

            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(new DateOnly(2024, 3, 15), (DateOnly)result.Rows[0][0]!);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Date_column_comparison_with_string_literal()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE events (id INT64 PRIMARY KEY, on_date DATE)");
            engine.Execute("INSERT INTO events VALUES (1, '2024-01-01')");
            engine.Execute("INSERT INTO events VALUES (2, '2024-06-15')");
            engine.Execute("INSERT INTO events VALUES (3, '2023-12-31')");

            var result = engine.Execute("SELECT id FROM events WHERE on_date >= '2024-01-01'");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToList();
            Assert.Equal([1L, 2L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Date_column_order_by()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, d DATE)");
            engine.Execute("INSERT INTO t VALUES (1, '2024-06-01')");
            engine.Execute("INSERT INTO t VALUES (2, '2024-01-15')");
            engine.Execute("INSERT INTO t VALUES (3, '2024-12-31')");

            var result = engine.Execute("SELECT id FROM t ORDER BY d ASC");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(2L, result.Rows[0][0]);
            Assert.Equal(1L, result.Rows[1][0]);
            Assert.Equal(3L, result.Rows[2][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Date_column_index_seek()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE bookings (id INT64 PRIMARY KEY, booked_on DATE)");
            engine.Execute("CREATE INDEX idx_date ON bookings (booked_on)");
            engine.Execute("INSERT INTO bookings VALUES (1, '2024-05-01')");
            engine.Execute("INSERT INTO bookings VALUES (2, '2024-06-01')");
            engine.Execute("INSERT INTO bookings VALUES (3, '2024-05-01')");

            var result = engine.Execute("SELECT id FROM bookings WHERE booked_on = '2024-05-01'");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => (long)r[0]!).OrderBy(x => x).ToList();
            Assert.Equal([1L, 3L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Date_database_persists_and_reopens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var engine = new QueryEngine(path))
            {
                engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, d DATE)");
                engine.Execute("INSERT INTO t VALUES (1, '2025-12-25')");
            }

            using var reopened = new QueryEngine(path);
            var result = reopened.Execute("SELECT d FROM t WHERE id = 1");

            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(new DateOnly(2025, 12, 25), (DateOnly)result.Rows[0][0]!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── DATETIME ──────────────────────────────────────────────────────────────

    [Fact]
    public void DateTime_column_accepts_iso8601_string_and_round_trips()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE log (id INT64 PRIMARY KEY, recorded_at DATETIME)");
            engine.Execute("INSERT INTO log VALUES (1, '2024-06-01T12:30:00.0000000Z')");

            var result = engine.Execute("SELECT recorded_at FROM log WHERE id = 1");

            Assert.Equal(1, result.Rows.Count);
            var dt = (DateTime)result.Rows[0][0]!;
            Assert.Equal(2024, dt.Year);
            Assert.Equal(6, dt.Month);
            Assert.Equal(1, dt.Day);
            Assert.Equal(12, dt.Hour);
            Assert.Equal(30, dt.Minute);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void DateTime_column_comparison_with_string_literal()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE log (id INT64 PRIMARY KEY, ts DATETIME)");
            engine.Execute("INSERT INTO log VALUES (1, '2024-01-01T00:00:00.0000000Z')");
            engine.Execute("INSERT INTO log VALUES (2, '2024-06-15T08:00:00.0000000Z')");
            engine.Execute("INSERT INTO log VALUES (3, '2023-12-31T23:59:59.0000000Z')");

            var result = engine.Execute("SELECT id FROM log WHERE ts > '2024-01-01T00:00:00.0000000Z'");

            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(2L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void DateTime_column_order_by()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, ts DATETIME)");
            engine.Execute("INSERT INTO t VALUES (1, '2024-06-01T12:00:00.0000000Z')");
            engine.Execute("INSERT INTO t VALUES (2, '2024-01-15T09:00:00.0000000Z')");
            engine.Execute("INSERT INTO t VALUES (3, '2024-12-31T23:59:59.0000000Z')");

            var result = engine.Execute("SELECT id FROM t ORDER BY ts ASC");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(2L, result.Rows[0][0]);
            Assert.Equal(1L, result.Rows[1][0]);
            Assert.Equal(3L, result.Rows[2][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void DateTime_database_persists_and_reopens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            var ts = new DateTime(2024, 9, 20, 15, 45, 30, DateTimeKind.Utc);
            using (var engine = new QueryEngine(path))
            {
                engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, ts DATETIME)");
                engine.Execute($"INSERT INTO t VALUES (1, '{ts:O}')");
            }

            using var reopened = new QueryEngine(path);
            var result = reopened.Execute("SELECT ts FROM t WHERE id = 1");

            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(ts, (DateTime)result.Rows[0][0]!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
