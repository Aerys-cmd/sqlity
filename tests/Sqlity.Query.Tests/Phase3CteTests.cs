namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for Phase 3 Common Table Expressions (CTEs):
/// WITH name AS (SELECT …) [, …] SELECT …
/// </summary>
public sealed class Phase3CteTests
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

    // ── Single CTE ────────────────────────────────────────────────────────────

    [Fact]
    public void Single_cte_filters_and_returns_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE employees (id INT64 PRIMARY KEY, name TEXT, dept TEXT, salary INT64)");
            engine.Execute("INSERT INTO employees VALUES (1, 'Alice', 'eng', 90000)");
            engine.Execute("INSERT INTO employees VALUES (2, 'Bob', 'hr', 50000)");
            engine.Execute("INSERT INTO employees VALUES (3, 'Carol', 'eng', 80000)");

            var result = engine.Execute(
                "WITH eng AS (SELECT id, name FROM employees WHERE dept = 'eng') " +
                "SELECT name FROM eng ORDER BY name");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal("Alice", result.Rows[0][0]);
            Assert.Equal("Carol", result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Single_cte_with_aggregation()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE sales (id INT64 PRIMARY KEY, amount INT64, region TEXT)");
            engine.Execute("INSERT INTO sales VALUES (1, 100, 'north')");
            engine.Execute("INSERT INTO sales VALUES (2, 200, 'north')");
            engine.Execute("INSERT INTO sales VALUES (3, 150, 'south')");

            var result = engine.Execute(
                "WITH totals AS (SELECT region, SUM(amount) AS total FROM sales GROUP BY region) " +
                "SELECT region, total FROM totals ORDER BY region");

            Assert.Equal(2, result.Rows.Count);
            var northRow = result.Rows.First(r => (string)r[0]! == "north");
            Assert.Equal(300L, northRow[1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Single_cte_result_can_be_filtered_in_outer_select()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, name TEXT, price INT64)");
            engine.Execute("INSERT INTO products VALUES (1, 'cheap', 5)");
            engine.Execute("INSERT INTO products VALUES (2, 'mid', 50)");
            engine.Execute("INSERT INTO products VALUES (3, 'expensive', 500)");

            var result = engine.Execute(
                "WITH p AS (SELECT id, name, price FROM products) " +
                "SELECT name FROM p WHERE price > 10 ORDER BY price");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal("mid", result.Rows[0][0]);
            Assert.Equal("expensive", result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Multiple CTEs ─────────────────────────────────────────────────────────

    [Fact]
    public void Multiple_ctes_each_define_independent_named_result_sets()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, cust_id INT64, total INT64)");
            engine.Execute("CREATE TABLE customers (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO customers VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO customers VALUES (2, 'Bob')");
            engine.Execute("INSERT INTO orders VALUES (1, 1, 100)");
            engine.Execute("INSERT INTO orders VALUES (2, 1, 200)");
            engine.Execute("INSERT INTO orders VALUES (3, 2, 50)");

            // Two CTEs: one filters customers, one filters orders.
            var result = engine.Execute(
                "WITH big_orders AS (SELECT cust_id FROM orders WHERE total > 75), " +
                "     good_customers AS (SELECT id, name FROM customers WHERE id = 1) " +
                "SELECT name FROM good_customers ORDER BY name");

            Assert.Single(result.Rows);
            Assert.Equal("Alice", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── CTE with LIMIT / ORDER BY ─────────────────────────────────────────────

    [Fact]
    public void Cte_with_limit_on_outer_select()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE nums (id INT64 PRIMARY KEY, n INT64)");
            for (var i = 1; i <= 10; i++)
                engine.Execute($"INSERT INTO nums VALUES ({i}, {i})");

            var result = engine.Execute(
                "WITH all_nums AS (SELECT n FROM nums) " +
                "SELECT n FROM all_nums ORDER BY n LIMIT 3");

            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal(2L, result.Rows[1][0]);
            Assert.Equal(3L, result.Rows[2][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── CTE is cleaned up (temp table doesn't persist) ───────────────────────

    [Fact]
    public void Cte_temp_table_is_not_visible_after_execution()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 42)");

            engine.Execute("WITH cte AS (SELECT v FROM t) SELECT v FROM cte");

            // __cte_cte should not appear in the table list after execution.
            var tables = engine.ListTables();
            Assert.DoesNotContain("__cte_cte", tables, StringComparer.OrdinalIgnoreCase);
        }
        finally { Cleanup(engine, path); }
    }

    // ── CTE returns empty result ───────────────────────────────────────────────

    [Fact]
    public void Cte_returning_no_rows_produces_empty_outer_result()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64)");
            engine.Execute("INSERT INTO t VALUES (1, 1)");

            var result = engine.Execute(
                "WITH empty_cte AS (SELECT v FROM t WHERE v > 999) " +
                "SELECT v FROM empty_cte");

            Assert.Empty(result.Rows);
        }
        finally { Cleanup(engine, path); }
    }
}
