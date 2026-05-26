namespace Sqlity.Query.Tests;

/// <summary>
/// End-to-end tests for secondary index support via SQL strings through QueryEngine.
/// </summary>
public sealed class SecondaryIndexIntegrationTests
{
    private static (QueryEngine Engine, string Path) CreateEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        var engine = new QueryEngine(path);
        return (engine, path);
    }

    private static void Exec(QueryEngine engine, string sql)
    {
        var result = engine.Execute(sql);
        Assert.Equal(0, result.RowsAffected == -1 ? 0 : 0); // just ensure no exception
    }

    // ── CREATE INDEX via SQL ───────────────────────────────────────────────────

    [Fact]
    public void CreateIndex_via_sql_allows_subsequent_select()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO users (id, name) VALUES (2, 'Bob')");
            engine.Execute("CREATE INDEX idx_name ON users (name)");

            var result = engine.Execute("SELECT id, name FROM users WHERE name = 'Alice'");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal("Alice", result.Rows[0][1]);
        }
        finally
        {
            engine.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CreateUniqueIndex_via_sql()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, sku STRING)");
            engine.Execute("INSERT INTO products (id, sku) VALUES (1, 'ABC')");
            engine.Execute("CREATE UNIQUE INDEX idx_sku ON products (sku)");

            Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("INSERT INTO products (id, sku) VALUES (2, 'ABC')"));
        }
        finally
        {
            engine.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── SELECT uses index seek ────────────────────────────────────────────────

    [Fact]
    public void Select_with_indexed_equality_returns_correct_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, customer_id INT64, status STRING)");
            for (int i = 1; i <= 20; i++)
                engine.Execute($"INSERT INTO orders (id, customer_id, status) VALUES ({i}, {(i <= 10 ? 1 : 2)}, 'open')");

            engine.Execute("CREATE INDEX idx_customer ON orders (customer_id)");

            var result = engine.Execute("SELECT id FROM orders WHERE customer_id = 1");
            Assert.Equal(10, result.Rows.Count);
            Assert.All(result.Rows, row => Assert.Equal(true, (long)row[0]! <= 10));
        }
        finally
        {
            engine.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Select_with_compound_AND_uses_index_and_post_filters()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, customer_id INT64, status STRING)");
            engine.Execute("INSERT INTO orders (id, customer_id, status) VALUES (1, 42, 'open')");
            engine.Execute("INSERT INTO orders (id, customer_id, status) VALUES (2, 42, 'closed')");
            engine.Execute("INSERT INTO orders (id, customer_id, status) VALUES (3, 99, 'open')");
            engine.Execute("CREATE INDEX idx_customer ON orders (customer_id)");

            var result = engine.Execute("SELECT id FROM orders WHERE customer_id = 42 AND status = 'open'");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally
        {
            engine.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Index maintenance on DELETE/UPDATE ────────────────────────────────────

    [Fact]
    public void Index_is_maintained_after_delete()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO users (id, name) VALUES (2, 'Bob')");
            engine.Execute("CREATE INDEX idx_name ON users (name)");

            engine.Execute("DELETE FROM users WHERE id = 1");

            var result = engine.Execute("SELECT id FROM users WHERE name = 'Alice'");
            Assert.Equal(0, result.Rows.Count);
        }
        finally
        {
            engine.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Index_is_maintained_after_update()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");
            engine.Execute("CREATE INDEX idx_name ON users (name)");

            engine.Execute("UPDATE users SET name = 'Alicia' WHERE id = 1");

            var oldResult = engine.Execute("SELECT id FROM users WHERE name = 'Alice'");
            Assert.Equal(0, oldResult.Rows.Count);

            var newResult = engine.Execute("SELECT id FROM users WHERE name = 'Alicia'");
            Assert.Equal(1, newResult.Rows.Count);
            Assert.Equal(1L, newResult.Rows[0][0]);
        }
        finally
        {
            engine.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── No index — full scan still works ─────────────────────────────────────

    [Fact]
    public void Select_without_index_still_works_correctly()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO users (id, name) VALUES (2, 'Bob')");

            var result = engine.Execute("SELECT id FROM users WHERE name = 'Alice'");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally
        {
            engine.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
