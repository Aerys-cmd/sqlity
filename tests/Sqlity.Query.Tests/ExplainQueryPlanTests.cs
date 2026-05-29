namespace Sqlity.Query.Tests;

/// <summary>
/// Tests for EXPLAIN QUERY PLAN statement.
/// Verifies that plan description rows are returned instead of data rows,
/// and that the correct access method (scan, seek, ordered scan) is reported.
/// </summary>
public sealed class ExplainQueryPlanTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

    // ── Full scan ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExplainQueryPlan_NoIndex_ReportsScanTable()
    {
        var path = TempPath();
        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, name STRING);");

            var result = engine.Execute("EXPLAIN QUERY PLAN SELECT id, name FROM products;");

            Assert.Equal(["id", "detail"], result.Columns);
            Assert.Single(result.Rows);
            var detail = (string?)result.Rows[0][1];
            Assert.NotNull(detail);
            Assert.StartsWith("SCAN TABLE products", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExplainQueryPlan_NoFilter_ReturnsIdColumnAsInt()
    {
        var path = TempPath();
        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY);");

            var result = engine.Execute("EXPLAIN QUERY PLAN SELECT id FROM t;");

            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Index seek ────────────────────────────────────────────────────────────

    [Fact]
    public void ExplainQueryPlan_IndexedColumn_ReportsSearchTable()
    {
        var path = TempPath();
        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, customer_id INT64);");
            engine.Execute("CREATE INDEX idx_orders_cust ON orders (customer_id);");
            engine.Execute("INSERT INTO orders VALUES (1, 42);");
            engine.Execute("INSERT INTO orders VALUES (2, 99);");
            engine.Execute("ANALYZE orders;");

            var result = engine.Execute("EXPLAIN QUERY PLAN SELECT id FROM orders WHERE customer_id = 42;");

            Assert.Single(result.Rows);
            var detail = (string?)result.Rows[0][1];
            Assert.NotNull(detail);
            Assert.StartsWith("SEARCH TABLE orders", detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("idx_orders_cust", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Ordered index scan ────────────────────────────────────────────────────

    [Fact]
    public void ExplainQueryPlan_OrderByIndexedColumn_ReportsIndexOrderBy()
    {
        var path = TempPath();
        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE employees (id INT64 PRIMARY KEY, salary INT64);");
            engine.Execute("CREATE INDEX idx_emp_salary ON employees (salary);");

            var result = engine.Execute("EXPLAIN QUERY PLAN SELECT id FROM employees ORDER BY salary;");

            Assert.Single(result.Rows);
            var detail = (string?)result.Rows[0][1];
            Assert.NotNull(detail);
            Assert.Contains("idx_emp_salary", detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ORDER BY", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── JOIN ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ExplainQueryPlan_Join_ReturnsTwoRows()
    {
        var path = TempPath();
        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("CREATE TABLE posts (id INT64 PRIMARY KEY, user_id INT64);");

            var result = engine.Execute(
                "EXPLAIN QUERY PLAN SELECT users.name FROM users JOIN posts ON users.id = posts.user_id;");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal(2L, result.Rows[1][0]);

            var joinDetail = (string?)result.Rows[1][1];
            Assert.NotNull(joinDetail);
            Assert.Contains("posts", joinDetail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("nested loop join", joinDetail, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Result columns ────────────────────────────────────────────────────────

    [Fact]
    public void ExplainQueryPlan_ResultHasExactlyTwoColumns()
    {
        var path = TempPath();
        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE t (x INT64 PRIMARY KEY);");

            var result = engine.Execute("EXPLAIN QUERY PLAN SELECT x FROM t;");

            Assert.Equal(2, result.Columns.Count);
            Assert.Equal("id", result.Columns[0]);
            Assert.Equal("detail", result.Columns[1]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Parse error for non-SELECT inner statement ────────────────────────────

    [Fact]
    public void ExplainQueryPlan_NonSelect_ThrowsParseError()
    {
        var path = TempPath();
        try
        {
            using var engine = new QueryEngine(path);
            Assert.Throws<InvalidOperationException>(
                () => engine.Execute("EXPLAIN QUERY PLAN INSERT INTO t VALUES (1);"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Row count hint appears after ANALYZE ──────────────────────────────────

    [Fact]
    public void ExplainQueryPlan_AfterAnalyze_IncludesRowCountHint()
    {
        var path = TempPath();
        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE items (id INT64 PRIMARY KEY, val STRING);");
            engine.Execute("INSERT INTO items VALUES (1, 'a');");
            engine.Execute("INSERT INTO items VALUES (2, 'b');");
            engine.Execute("ANALYZE items;");

            var result = engine.Execute("EXPLAIN QUERY PLAN SELECT id FROM items;");

            var detail = (string?)result.Rows[0][1];
            Assert.NotNull(detail);
            Assert.Contains("~2 rows", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
