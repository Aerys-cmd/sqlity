namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for the "Wider SQL surface" roadmap item:
/// ORDER BY, LIMIT/OFFSET, aggregate functions (COUNT/SUM/MIN/MAX/AVG), GROUP BY, and HAVING.
/// </summary>
public sealed class WiderSqlSurfaceTests
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

    // ── ORDER BY ──────────────────────────────────────────────────────────────

    [Fact]
    public void OrderBy_single_column_asc_returns_rows_in_ascending_order()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, price INT64)");
            engine.Execute("INSERT INTO products VALUES (1, 30)");
            engine.Execute("INSERT INTO products VALUES (2, 10)");
            engine.Execute("INSERT INTO products VALUES (3, 20)");

            var result = engine.Execute("SELECT id, price FROM products ORDER BY price ASC");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(10L, result.Rows[0][1]);
            Assert.Equal(20L, result.Rows[1][1]);
            Assert.Equal(30L, result.Rows[2][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void OrderBy_single_column_desc_returns_rows_in_descending_order()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE scores (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO scores VALUES (1, 50)");
            engine.Execute("INSERT INTO scores VALUES (2, 90)");
            engine.Execute("INSERT INTO scores VALUES (3, 70)");

            var result = engine.Execute("SELECT id, score FROM scores ORDER BY score DESC");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(90L, result.Rows[0][1]);
            Assert.Equal(70L, result.Rows[1][1]);
            Assert.Equal(50L, result.Rows[2][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void OrderBy_multi_column_sorts_by_primary_then_secondary()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE employees (id INT64 PRIMARY KEY, dept INT64, salary INT64)");
            engine.Execute("INSERT INTO employees VALUES (1, 2, 80)");
            engine.Execute("INSERT INTO employees VALUES (2, 1, 60)");
            engine.Execute("INSERT INTO employees VALUES (3, 1, 90)");
            engine.Execute("INSERT INTO employees VALUES (4, 2, 50)");

            var result = engine.Execute("SELECT id, dept, salary FROM employees ORDER BY dept ASC, salary DESC");

            Assert.Equal(4, result.Rows.Count);
            // dept=1 comes first, sorted by salary DESC within dept=1
            Assert.Equal(1L, result.Rows[0][1]); Assert.Equal(90L, result.Rows[0][2]);
            Assert.Equal(1L, result.Rows[1][1]); Assert.Equal(60L, result.Rows[1][2]);
            // dept=2 comes next, sorted by salary DESC within dept=2
            Assert.Equal(2L, result.Rows[2][1]); Assert.Equal(80L, result.Rows[2][2]);
            Assert.Equal(2L, result.Rows[3][1]); Assert.Equal(50L, result.Rows[3][2]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void OrderBy_with_secondary_index_returns_correct_row_order()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE items (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO items VALUES (1, 'Zebra')");
            engine.Execute("INSERT INTO items VALUES (2, 'Apple')");
            engine.Execute("INSERT INTO items VALUES (3, 'Mango')");
            engine.Execute("CREATE INDEX idx_name ON items (name)");

            var result = engine.Execute("SELECT id, name FROM items ORDER BY name ASC");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal("Apple", result.Rows[0][1]);
            Assert.Equal("Mango", result.Rows[1][1]);
            Assert.Equal("Zebra", result.Rows[2][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void OrderBy_with_secondary_index_desc_returns_reverse_order()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE items (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO items VALUES (1, 'Zebra')");
            engine.Execute("INSERT INTO items VALUES (2, 'Apple')");
            engine.Execute("INSERT INTO items VALUES (3, 'Mango')");
            engine.Execute("CREATE INDEX idx_name ON items (name)");

            var result = engine.Execute("SELECT id, name FROM items ORDER BY name DESC");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal("Zebra", result.Rows[0][1]);
            Assert.Equal("Mango", result.Rows[1][1]);
            Assert.Equal("Apple", result.Rows[2][1]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── LIMIT / OFFSET ────────────────────────────────────────────────────────

    [Fact]
    public void Limit_without_offset_returns_first_n_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE nums (id INT64 PRIMARY KEY, val INT64)");
            for (int i = 1; i <= 5; i++)
                engine.Execute($"INSERT INTO nums VALUES ({i}, {i * 10})");

            var result = engine.Execute("SELECT id, val FROM nums ORDER BY val ASC LIMIT 3");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(10L, result.Rows[0][1]);
            Assert.Equal(20L, result.Rows[1][1]);
            Assert.Equal(30L, result.Rows[2][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Offset_without_limit_skips_first_n_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE nums (id INT64 PRIMARY KEY, val INT64)");
            for (int i = 1; i <= 5; i++)
                engine.Execute($"INSERT INTO nums VALUES ({i}, {i * 10})");

            var result = engine.Execute("SELECT id, val FROM nums ORDER BY val ASC OFFSET 3");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(40L, result.Rows[0][1]);
            Assert.Equal(50L, result.Rows[1][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Limit_and_offset_together_returns_correct_page()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE nums (id INT64 PRIMARY KEY, val INT64)");
            for (int i = 1; i <= 10; i++)
                engine.Execute($"INSERT INTO nums VALUES ({i}, {i * 10})");

            var result = engine.Execute("SELECT id, val FROM nums ORDER BY val ASC LIMIT 3 OFFSET 4");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(50L, result.Rows[0][1]);
            Assert.Equal(60L, result.Rows[1][1]);
            Assert.Equal(70L, result.Rows[2][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Select_star_with_order_by_and_limit_returns_correct_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO users VALUES (1, 'Charlie')");
            engine.Execute("INSERT INTO users VALUES (2, 'Alice')");
            engine.Execute("INSERT INTO users VALUES (3, 'Bob')");

            var result = engine.Execute("SELECT * FROM users ORDER BY name ASC LIMIT 2");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal("Alice", result.Rows[0][1]);
            Assert.Equal("Bob", result.Rows[1][1]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── AGGREGATE FUNCTIONS ───────────────────────────────────────────────────

    [Fact]
    public void Count_star_returns_total_row_count()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 100)");
            engine.Execute("INSERT INTO t VALUES (2, 200)");
            engine.Execute("INSERT INTO t VALUES (3, 300)");

            var result = engine.Execute("SELECT COUNT(*) FROM t");

            Assert.Single(result.Rows);
            Assert.Equal(new[] { "Count(*)" }, result.Columns);
            Assert.Equal(3L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Count_star_on_empty_table_returns_zero()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE empty (id INT64 PRIMARY KEY)");

            var result = engine.Execute("SELECT COUNT(*) FROM empty");

            Assert.Single(result.Rows);
            Assert.Equal(0L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Count_column_skips_null_values()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, tag STRING)");
            engine.Execute("INSERT INTO t VALUES (1, 'a')");
            engine.Execute("INSERT INTO t VALUES (2, NULL)");
            engine.Execute("INSERT INTO t VALUES (3, 'b')");

            var result = engine.Execute("SELECT COUNT(tag) FROM t");

            Assert.Single(result.Rows);
            Assert.Equal(new[] { "Count(tag)" }, result.Columns);
            Assert.Equal(2L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Sum_returns_total_of_column_values()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, amount INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 100)");
            engine.Execute("INSERT INTO t VALUES (2, 250)");
            engine.Execute("INSERT INTO t VALUES (3, 50)");

            var result = engine.Execute("SELECT SUM(amount) FROM t");

            Assert.Single(result.Rows);
            Assert.Equal(new[] { "Sum(amount)" }, result.Columns);
            Assert.Equal(400L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Min_and_max_return_extreme_values()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 30)");
            engine.Execute("INSERT INTO t VALUES (2, 90)");
            engine.Execute("INSERT INTO t VALUES (3, 60)");

            var minResult = engine.Execute("SELECT MIN(score) FROM t");
            var maxResult = engine.Execute("SELECT MAX(score) FROM t");

            Assert.Equal(new[] { "Min(score)" }, minResult.Columns);
            Assert.Equal(30L, minResult.Rows[0][0]);

            Assert.Equal(new[] { "Max(score)" }, maxResult.Columns);
            Assert.Equal(90L, maxResult.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Avg_returns_double_result()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 10)");
            engine.Execute("INSERT INTO t VALUES (2, 20)");
            engine.Execute("INSERT INTO t VALUES (3, 30)");

            var result = engine.Execute("SELECT AVG(val) FROM t");

            Assert.Single(result.Rows);
            Assert.Equal(new[] { "Avg(val)" }, result.Columns);
            var avg = Assert.IsType<double>(result.Rows[0][0]);
            Assert.Equal(20.0, avg, precision: 10);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Aggregate_ignores_null_values_in_sum_and_avg()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 10)");
            engine.Execute("INSERT INTO t VALUES (2, NULL)");
            engine.Execute("INSERT INTO t VALUES (3, 30)");

            var sumResult = engine.Execute("SELECT SUM(val) FROM t");
            var avgResult = engine.Execute("SELECT AVG(val) FROM t");

            Assert.Equal(40L, sumResult.Rows[0][0]);
            Assert.Equal(20.0, (double)avgResult.Rows[0][0]!, precision: 10);
        }
        finally { Cleanup(engine, path); }
    }

    // ── GROUP BY ──────────────────────────────────────────────────────────────

    [Fact]
    public void GroupBy_single_column_produces_per_group_count()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, status STRING)");
            engine.Execute("INSERT INTO orders VALUES (1, 'open')");
            engine.Execute("INSERT INTO orders VALUES (2, 'open')");
            engine.Execute("INSERT INTO orders VALUES (3, 'closed')");
            engine.Execute("INSERT INTO orders VALUES (4, 'open')");

            var result = engine.Execute("SELECT status, COUNT(*) FROM orders GROUP BY status");

            Assert.Equal(2, result.Rows.Count);
            var openRow = result.Rows.Single(r => (string)r[0]! == "open");
            var closedRow = result.Rows.Single(r => (string)r[0]! == "closed");
            Assert.Equal(3L, openRow[1]);
            Assert.Equal(1L, closedRow[1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void GroupBy_multi_column_produces_correct_groups()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE sales (id INT64 PRIMARY KEY, region STRING, category STRING, amount INT64)");
            engine.Execute("INSERT INTO sales VALUES (1, 'north', 'food', 100)");
            engine.Execute("INSERT INTO sales VALUES (2, 'north', 'food', 200)");
            engine.Execute("INSERT INTO sales VALUES (3, 'north', 'tech', 300)");
            engine.Execute("INSERT INTO sales VALUES (4, 'south', 'food', 150)");

            var result = engine.Execute("SELECT region, category, SUM(amount) FROM sales GROUP BY region, category");

            Assert.Equal(3, result.Rows.Count);
            var northFood = result.Rows.Single(r => (string)r[0]! == "north" && (string)r[1]! == "food");
            var northTech = result.Rows.Single(r => (string)r[0]! == "north" && (string)r[1]! == "tech");
            var southFood = result.Rows.Single(r => (string)r[0]! == "south" && (string)r[1]! == "food");
            Assert.Equal(300L, northFood[2]);
            Assert.Equal(300L, northTech[2]);
            Assert.Equal(150L, southFood[2]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void GroupBy_strict_enforcement_throws_when_column_not_in_group_by()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, dept STRING, salary INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 'eng', 100)");

            Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("SELECT dept, salary, COUNT(*) FROM t GROUP BY dept"));
        }
        finally { Cleanup(engine, path); }
    }

    // ── HAVING ────────────────────────────────────────────────────────────────

    [Fact]
    public void Having_filters_groups_by_count()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, customer_id INT64)");
            engine.Execute("INSERT INTO orders VALUES (1, 10)");
            engine.Execute("INSERT INTO orders VALUES (2, 10)");
            engine.Execute("INSERT INTO orders VALUES (3, 10)");
            engine.Execute("INSERT INTO orders VALUES (4, 20)");
            engine.Execute("INSERT INTO orders VALUES (5, 30)");
            engine.Execute("INSERT INTO orders VALUES (6, 30)");

            var result = engine.Execute("SELECT customer_id, COUNT(*) FROM orders GROUP BY customer_id HAVING COUNT(*) > 2");

            Assert.Single(result.Rows);
            Assert.Equal(10L, result.Rows[0][0]);
            Assert.Equal(3L, result.Rows[0][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Having_with_aggregate_not_in_select_still_filters_correctly()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, dept STRING, salary INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 'eng', 80)");
            engine.Execute("INSERT INTO t VALUES (2, 'eng', 100)");
            engine.Execute("INSERT INTO t VALUES (3, 'hr', 60)");

            // SUM(salary) is not in the SELECT list, but used in HAVING
            var result = engine.Execute("SELECT dept, COUNT(*) FROM t GROUP BY dept HAVING SUM(salary) > 100");

            Assert.Single(result.Rows);
            Assert.Equal("eng", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }
}
