namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for Phase 3 EXISTS / NOT EXISTS as WHERE atoms.
/// </summary>
public sealed class Phase3ExistsTests
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

    // ── EXISTS: subquery returns rows → condition is true ─────────────────────

    [Fact]
    public void Exists_with_rows_returns_matching_outer_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, customer_id INT64)");
            engine.Execute("CREATE TABLE customers (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("INSERT INTO customers VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO customers VALUES (2, 'Bob')");
            engine.Execute("INSERT INTO orders VALUES (10, 1)");

            // Alice has an order, Bob does not.
            var result = engine.Execute(
                "SELECT name FROM customers WHERE EXISTS (SELECT id FROM orders WHERE customer_id = 1) ORDER BY name");

            // EXISTS evaluates to true for every outer row (non-correlated), so both pass
            Assert.Equal(2, result.Rows.Count);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Exists_when_subquery_returns_no_rows_filters_out_all_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, name TEXT)");
            engine.Execute("CREATE TABLE stock (product_id INT64 PRIMARY KEY, qty INT64)");
            engine.Execute("INSERT INTO products VALUES (1, 'Widget')");
            engine.Execute("INSERT INTO products VALUES (2, 'Gadget')");
            // stock table is empty

            var result = engine.Execute(
                "SELECT name FROM products WHERE EXISTS (SELECT product_id FROM stock WHERE qty > 0)");

            Assert.Empty(result.Rows);
        }
        finally { Cleanup(engine, path); }
    }

    // ── NOT EXISTS: inverse of EXISTS ─────────────────────────────────────────

    [Fact]
    public void Not_exists_with_rows_filters_out_all_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE items (id INT64 PRIMARY KEY)");
            engine.Execute("CREATE TABLE flags (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO items VALUES (1)");
            engine.Execute("INSERT INTO items VALUES (2)");
            engine.Execute("INSERT INTO flags VALUES (99)"); // subquery will return a row

            var result = engine.Execute(
                "SELECT id FROM items WHERE NOT EXISTS (SELECT id FROM flags)");

            Assert.Empty(result.Rows);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Not_exists_when_subquery_empty_passes_all_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE items (id INT64 PRIMARY KEY)");
            engine.Execute("CREATE TABLE flags (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO items VALUES (1)");
            engine.Execute("INSERT INTO items VALUES (2)");
            // flags is empty → NOT EXISTS is true for all outer rows

            var result = engine.Execute(
                "SELECT id FROM items WHERE NOT EXISTS (SELECT id FROM flags) ORDER BY id");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal(2L, result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── EXISTS combined with AND ───────────────────────────────────────────────

    [Fact]
    public void Exists_combined_with_and_condition()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE employees (id INT64 PRIMARY KEY, dept TEXT, salary INT64)");
            engine.Execute("CREATE TABLE bonuses (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO employees VALUES (1, 'eng', 100000)");
            engine.Execute("INSERT INTO employees VALUES (2, 'eng', 60000)");
            engine.Execute("INSERT INTO employees VALUES (3, 'hr', 80000)");
            engine.Execute("INSERT INTO bonuses VALUES (1)"); // bonus pool exists

            // EXISTS is true (bonuses has a row) AND salary > 70000
            var result = engine.Execute(
                "SELECT id FROM employees WHERE EXISTS (SELECT id FROM bonuses) AND salary > 70000 ORDER BY id");

            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal(3L, result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── EXISTS combined with OR ────────────────────────────────────────────────

    [Fact]
    public void Exists_combined_with_or_condition()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, role TEXT)");
            engine.Execute("CREATE TABLE admins (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO users VALUES (1, 'guest')");
            engine.Execute("INSERT INTO users VALUES (2, 'guest')");
            // admins is empty → EXISTS is false, but role = 'guest' is true for both

            var result = engine.Execute(
                "SELECT id FROM users WHERE EXISTS (SELECT id FROM admins) OR role = 'guest' ORDER BY id");

            Assert.Equal(2, result.Rows.Count);
        }
        finally { Cleanup(engine, path); }
    }

    // ── EXISTS in DELETE WHERE ─────────────────────────────────────────────────

    [Fact]
    public void Exists_in_delete_where_deletes_all_rows_when_subquery_has_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE drafts (id INT64 PRIMARY KEY, content TEXT)");
            engine.Execute("CREATE TABLE publish_flag (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO drafts VALUES (1, 'hello')");
            engine.Execute("INSERT INTO drafts VALUES (2, 'world')");
            engine.Execute("INSERT INTO publish_flag VALUES (1)");

            engine.Execute("DELETE FROM drafts WHERE EXISTS (SELECT id FROM publish_flag)");

            var result = engine.Execute("SELECT id FROM drafts");
            Assert.Empty(result.Rows);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Exists_in_delete_where_deletes_nothing_when_subquery_empty()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE drafts (id INT64 PRIMARY KEY)");
            engine.Execute("CREATE TABLE publish_flag (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO drafts VALUES (1)");
            engine.Execute("INSERT INTO drafts VALUES (2)");
            // publish_flag is empty → EXISTS is false → nothing deleted

            engine.Execute("DELETE FROM drafts WHERE EXISTS (SELECT id FROM publish_flag)");

            var result = engine.Execute("SELECT id FROM drafts");
            Assert.Equal(2, result.Rows.Count);
        }
        finally { Cleanup(engine, path); }
    }

    // ── EXISTS in UPDATE WHERE ────────────────────────────────────────────────

    [Fact]
    public void Exists_in_update_where_updates_all_rows_when_subquery_has_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE tasks (id INT64 PRIMARY KEY, status TEXT)");
            engine.Execute("CREATE TABLE trigger_table (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO tasks VALUES (1, 'pending')");
            engine.Execute("INSERT INTO tasks VALUES (2, 'pending')");
            engine.Execute("INSERT INTO trigger_table VALUES (1)");

            engine.Execute("UPDATE tasks SET status = 'done' WHERE EXISTS (SELECT id FROM trigger_table)");

            var result = engine.Execute("SELECT status FROM tasks ORDER BY id");
            Assert.All(result.Rows, row => Assert.Equal("done", row[0]));
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Exists_in_update_where_updates_nothing_when_subquery_empty()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE tasks (id INT64 PRIMARY KEY, status TEXT)");
            engine.Execute("CREATE TABLE trigger_table (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO tasks VALUES (1, 'pending')");
            // trigger_table is empty → EXISTS is false → nothing updated

            engine.Execute("UPDATE tasks SET status = 'done' WHERE EXISTS (SELECT id FROM trigger_table)");

            var result = engine.Execute("SELECT status FROM tasks");
            Assert.Equal("pending", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }
}
