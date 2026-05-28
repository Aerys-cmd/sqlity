namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for scalar subqueries and IN (subquery) — §5 of the roadmap.
/// </summary>
public sealed class SubqueryTests
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

    // ── IN (subquery) ─────────────────────────────────────────────────────────

    [Fact]
    public void InSubquery_filters_rows_whose_column_value_is_in_the_subquery_result()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE customers (id INT64 PRIMARY KEY, name STRING NOT NULL)");
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, customer_id INT64 NOT NULL)");
            engine.Execute("INSERT INTO customers VALUES (1, 'Alice')");
            engine.Execute("INSERT INTO customers VALUES (2, 'Bob')");
            engine.Execute("INSERT INTO customers VALUES (3, 'Carol')");
            engine.Execute("INSERT INTO orders VALUES (1, 1)");
            engine.Execute("INSERT INTO orders VALUES (2, 3)");

            var result = engine.Execute(
                "SELECT id, name FROM customers WHERE id IN (SELECT customer_id FROM orders)");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => r[0]).Cast<long>().OrderBy(x => x).ToArray();
            Assert.Equal([1L, 3L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void InSubquery_returns_empty_when_subquery_produces_no_matching_values()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, category_id INT64)");
            engine.Execute("CREATE TABLE active_categories (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO products VALUES (1, 10)");
            engine.Execute("INSERT INTO products VALUES (2, 20)");
            // active_categories is empty → no products should match

            var result = engine.Execute(
                "SELECT id FROM products WHERE category_id IN (SELECT id FROM active_categories)");

            Assert.Empty(result.Rows);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void InSubquery_in_DELETE_removes_only_matching_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE items (id INT64 PRIMARY KEY, tag INT64)");
            engine.Execute("CREATE TABLE tags_to_delete (tag_id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO items VALUES (1, 10)");
            engine.Execute("INSERT INTO items VALUES (2, 20)");
            engine.Execute("INSERT INTO items VALUES (3, 10)");
            engine.Execute("INSERT INTO tags_to_delete VALUES (10)");

            engine.Execute("DELETE FROM items WHERE tag IN (SELECT tag_id FROM tags_to_delete)");

            var result = engine.Execute("SELECT id FROM items");
            Assert.Single(result.Rows);
            Assert.Equal(2L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void InSubquery_in_UPDATE_modifies_only_matching_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE employees (id INT64 PRIMARY KEY, salary INT64, dept_id INT64)");
            engine.Execute("CREATE TABLE bonus_depts (dept_id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO employees VALUES (1, 1000, 5)");
            engine.Execute("INSERT INTO employees VALUES (2, 1000, 7)");
            engine.Execute("INSERT INTO employees VALUES (3, 1000, 5)");
            engine.Execute("INSERT INTO bonus_depts VALUES (5)");

            engine.Execute("UPDATE employees SET salary = 2000 WHERE dept_id IN (SELECT dept_id FROM bonus_depts)");

            var raised = engine.Execute("SELECT id, salary FROM employees WHERE salary = 2000");
            Assert.Equal(2, raised.Rows.Count);
            var raisedIds = raised.Rows.Select(r => r[0]).Cast<long>().OrderBy(x => x).ToArray();
            Assert.Equal([1L, 3L], raisedIds);

            var unchanged = engine.Execute("SELECT id, salary FROM employees WHERE salary = 1000");
            Assert.Single(unchanged.Rows);
            Assert.Equal(2L, unchanged.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Scalar subquery ───────────────────────────────────────────────────────

    [Fact]
    public void ScalarSubquery_equality_filters_rows_matching_the_computed_value()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, price INT64)");
            engine.Execute("CREATE TABLE thresholds (id INT64 PRIMARY KEY, value INT64)");
            engine.Execute("INSERT INTO products VALUES (1, 10)");
            engine.Execute("INSERT INTO products VALUES (2, 20)");
            engine.Execute("INSERT INTO products VALUES (3, 30)");
            engine.Execute("INSERT INTO thresholds VALUES (1, 20)");

            var result = engine.Execute(
                "SELECT id FROM products WHERE price = (SELECT value FROM thresholds WHERE id = 1)");

            Assert.Single(result.Rows);
            Assert.Equal(2L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void ScalarSubquery_greater_than_filters_rows_above_the_computed_value()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE scores (id INT64 PRIMARY KEY, score INT64)");
            engine.Execute("INSERT INTO scores VALUES (1, 40)");
            engine.Execute("INSERT INTO scores VALUES (2, 60)");
            engine.Execute("INSERT INTO scores VALUES (3, 80)");

            // Subquery computes MAX which is 80; rows with score > 80 → none? Let's use AVG-style manual pick.
            // Simpler: pick a known row's value as scalar.
            var result = engine.Execute(
                "SELECT id FROM scores WHERE score > (SELECT score FROM scores WHERE id = 1)");

            Assert.Equal(2, result.Rows.Count);
            var ids = result.Rows.Select(r => r[0]).Cast<long>().OrderBy(x => x).ToArray();
            Assert.Equal([2L, 3L], ids);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void ScalarSubquery_returning_no_rows_evaluates_comparison_as_false()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE values_table (id INT64 PRIMARY KEY, val INT64)");
            engine.Execute("INSERT INTO values_table VALUES (1, 100)");
            engine.Execute("INSERT INTO values_table VALUES (2, 200)");

            // Subquery returns no rows because id = 999 doesn't exist → comparison is always false
            var result = engine.Execute(
                "SELECT id FROM values_table WHERE val = (SELECT val FROM values_table WHERE id = 999)");

            Assert.Empty(result.Rows);
        }
        finally { Cleanup(engine, path); }
    }
}
