namespace Sqlity.Query.Tests;

/// <summary>
/// Integration tests for DROP TABLE and ALTER TABLE DDL statements.
/// </summary>
public sealed class DdlTests
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

    // ── DROP TABLE ─────────────────────────────────────────────────────────────

    [Fact]
    public void DropTable_removes_table_and_all_data()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO users (id, name) VALUES (1, 'Alice')");
            engine.Execute("DROP TABLE users");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("SELECT id FROM users"));
            Assert.Contains("does not exist", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void DropTable_on_nonexistent_table_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("DROP TABLE no_such_table"));
            Assert.Contains("does not exist", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void DropTable_removes_associated_indexes()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("CREATE INDEX idx_name ON products (name)");
            engine.Execute("INSERT INTO products (id, name) VALUES (1, 'Widget')");
            engine.Execute("DROP TABLE products");

            // Recreate the same table — the index must not linger
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO products (id, name) VALUES (2, 'Gadget')");
            var result = engine.Execute("SELECT id FROM products");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(2L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void DropTable_and_recreate_with_same_name_works()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO t (id) VALUES (1)");
            engine.Execute("DROP TABLE t");
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, value STRING)");
            engine.Execute("INSERT INTO t (id, value) VALUES (10, 'hello')");

            var result = engine.Execute("SELECT id, value FROM t");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(10L, result.Rows[0][0]);
            Assert.Equal("hello", result.Rows[0][1]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── ALTER TABLE RENAME TO ──────────────────────────────────────────────────

    [Fact]
    public void AlterTable_rename_makes_old_name_inaccessible()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE old_name (id INT64 PRIMARY KEY)");
            engine.Execute("ALTER TABLE old_name RENAME TO new_name");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("SELECT id FROM old_name"));
            Assert.Contains("does not exist", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_rename_data_accessible_via_new_name()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE old_t (id INT64 PRIMARY KEY, v STRING)");
            engine.Execute("INSERT INTO old_t (id, v) VALUES (1, 'hello')");
            engine.Execute("ALTER TABLE old_t RENAME TO new_t");

            var result = engine.Execute("SELECT id, v FROM new_t");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal("hello", result.Rows[0][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_rename_on_nonexistent_table_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("ALTER TABLE ghost RENAME TO whatever"));
            Assert.Contains("does not exist", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_rename_to_existing_name_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY)");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY)");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("ALTER TABLE a RENAME TO b"));
            Assert.Contains("already exists", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_rename_updates_associated_indexes()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("CREATE INDEX idx_name ON t (name)");
            engine.Execute("INSERT INTO t (id, name) VALUES (1, 'Alice')");
            engine.Execute("ALTER TABLE t RENAME TO people");

            // Index should work via the new table name
            var result = engine.Execute("SELECT id FROM people WHERE name = 'Alice'");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── ALTER TABLE ADD COLUMN ─────────────────────────────────────────────────

    [Fact]
    public void AlterTable_add_nullable_column_existing_rows_return_null()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO t (id) VALUES (1)");
            engine.Execute("ALTER TABLE t ADD COLUMN extra STRING");

            var result = engine.Execute("SELECT id, extra FROM t");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Null(result.Rows[0][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_add_column_new_rows_can_supply_value()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO t (id) VALUES (1)");
            engine.Execute("ALTER TABLE t ADD COLUMN extra STRING");
            engine.Execute("INSERT INTO t (id, extra) VALUES (2, 'hello')");

            var result = engine.Execute("SELECT id, extra FROM t ORDER BY id");
            Assert.Equal(2, result.Rows.Count);
            Assert.Null(result.Rows[0][1]);
            Assert.Equal("hello", result.Rows[1][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_add_not_null_column_to_nonempty_table_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY)");
            engine.Execute("INSERT INTO t (id) VALUES (1)");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("ALTER TABLE t ADD COLUMN required STRING NOT NULL"));
            Assert.Contains("NOT NULL", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_add_not_null_column_to_empty_table_succeeds()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY)");
            engine.Execute("ALTER TABLE t ADD COLUMN required STRING NOT NULL");
            engine.Execute("INSERT INTO t (id, required) VALUES (1, 'val')");

            var result = engine.Execute("SELECT required FROM t");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal("val", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_add_duplicate_column_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name STRING)");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("ALTER TABLE t ADD COLUMN name STRING"));
            Assert.Contains("already exists", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    // ── ALTER TABLE RENAME COLUMN ──────────────────────────────────────────────

    [Fact]
    public void AlterTable_rename_column_old_name_fails_in_select()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, old_col STRING)");
            engine.Execute("ALTER TABLE t RENAME COLUMN old_col TO new_col");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("SELECT old_col FROM t"));
            Assert.Contains("old_col", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_rename_column_data_accessible_via_new_name()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, old_col STRING)");
            engine.Execute("INSERT INTO t (id, old_col) VALUES (1, 'data')");
            engine.Execute("ALTER TABLE t RENAME COLUMN old_col TO new_col");

            var result = engine.Execute("SELECT new_col FROM t");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal("data", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_rename_nonexistent_column_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY)");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("ALTER TABLE t RENAME COLUMN ghost TO whatever"));
            Assert.Contains("does not exist", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_rename_column_to_existing_name_throws()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, a STRING, b STRING)");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("ALTER TABLE t RENAME COLUMN a TO b"));
            Assert.Contains("already exists", ex.Message);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void AlterTable_rename_column_updates_index_metadata()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, old_col STRING)");
            engine.Execute("CREATE INDEX idx ON t (old_col)");
            engine.Execute("INSERT INTO t (id, old_col) VALUES (1, 'value')");
            engine.Execute("ALTER TABLE t RENAME COLUMN old_col TO new_col");

            // Index should still work with the renamed column
            var result = engine.Execute("SELECT id FROM t WHERE new_col = 'value'");
            Assert.Equal(1, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Phase 2: DEFAULT expr ──────────────────────────────────────────────────

    [Fact]
    public void Default_value_is_applied_when_column_omitted()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, status STRING DEFAULT 'active')");
            engine.Execute("INSERT INTO t (id) VALUES (1)");
            var result = engine.Execute("SELECT status FROM t WHERE id = 1");
            Assert.Single(result.Rows);
            Assert.Equal("active", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Default_integer_value_is_applied_when_column_omitted()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64 DEFAULT 0)");
            engine.Execute("INSERT INTO t (id) VALUES (1)");
            var result = engine.Execute("SELECT score FROM t WHERE id = 1");
            Assert.Single(result.Rows);
            Assert.Equal(0L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Nullable_column_with_no_default_gets_null_when_omitted()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, note STRING)");
            engine.Execute("INSERT INTO t (id) VALUES (1)");
            var result = engine.Execute("SELECT note FROM t WHERE id = 1");
            Assert.Single(result.Rows);
            Assert.Null(result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Phase 2: AUTOINCREMENT / SERIAL ──────────────────────────────────────

    [Fact]
    public void Autoincrement_assigns_next_key_when_id_omitted()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY AUTOINCREMENT, name STRING)");
            engine.Execute("INSERT INTO t (name) VALUES ('Alice')");
            engine.Execute("INSERT INTO t (name) VALUES ('Bob')");
            var result = engine.Execute("SELECT id FROM t ORDER BY id");
            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal(2L, result.Rows[1][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Serial_alias_assigns_next_key_when_id_omitted()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY SERIAL, name STRING)");
            engine.Execute("INSERT INTO t (name) VALUES ('Alice')");
            var result = engine.Execute("SELECT id FROM t");
            Assert.Single(result.Rows);
            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Phase 2: Inline UNIQUE ────────────────────────────────────────────────

    [Fact]
    public void Unique_constraint_rejects_duplicate_values()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, email STRING UNIQUE NOT NULL)");
            engine.Execute("INSERT INTO t VALUES (1, 'a@b.com')");
            Assert.Throws<InvalidOperationException>(() => engine.Execute("INSERT INTO t VALUES (2, 'a@b.com')"));
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void Unique_constraint_allows_distinct_values()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, code STRING UNIQUE NOT NULL)");
            engine.Execute("INSERT INTO t VALUES (1, 'X1')");
            engine.Execute("INSERT INTO t VALUES (2, 'X2')");
            var result = engine.Execute("SELECT id FROM t ORDER BY id");
            Assert.Equal(2, result.Rows.Count);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Phase 2: INSERT OR REPLACE ────────────────────────────────────────────

    [Fact]
    public void InsertOrReplace_overwrites_existing_row()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO t VALUES (1, 'Alice')");
            engine.Execute("INSERT OR REPLACE INTO t VALUES (1, 'Alicia')");
            var result = engine.Execute("SELECT name FROM t WHERE id = 1");
            Assert.Single(result.Rows);
            Assert.Equal("Alicia", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void InsertOrReplace_inserts_new_row_when_no_conflict()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT OR REPLACE INTO t VALUES (1, 'Alice')");
            var result = engine.Execute("SELECT name FROM t WHERE id = 1");
            Assert.Single(result.Rows);
            Assert.Equal("Alice", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Phase 2: INSERT INTO t SELECT ─────────────────────────────────────────

    [Fact]
    public void InsertFromSelect_copies_rows_between_tables()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE src (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("CREATE TABLE dst (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO src VALUES (1, 'Alice'), (2, 'Bob')");
            engine.Execute("INSERT INTO dst SELECT id, name FROM src");
            var result = engine.Execute("SELECT id, name FROM dst ORDER BY id");
            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal("Alice", result.Rows[0][1]);
            Assert.Equal(2L, result.Rows[1][0]);
            Assert.Equal("Bob", result.Rows[1][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void InsertFromSelect_with_column_list_maps_by_position()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE src (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("CREATE TABLE dst (id INT64 PRIMARY KEY, label STRING)");
            engine.Execute("INSERT INTO src VALUES (1, 'Hello')");
            engine.Execute("INSERT INTO dst (id, label) SELECT id, name FROM src");
            var result = engine.Execute("SELECT label FROM dst WHERE id = 1");
            Assert.Single(result.Rows);
            Assert.Equal("Hello", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Phase 2: CREATE VIEW ──────────────────────────────────────────────────

    [Fact]
    public void CreateView_and_select_from_view()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, active BOOLEAN)");
            engine.Execute("INSERT INTO users VALUES (1, 'Alice', TRUE)");
            engine.Execute("INSERT INTO users VALUES (2, 'Bob', FALSE)");
            engine.Execute("CREATE VIEW active_users AS SELECT id, name FROM users WHERE active = TRUE");
            var result = engine.Execute("SELECT id, name FROM active_users");
            Assert.Single(result.Rows);
            Assert.Equal(1L, result.Rows[0][0]);
            Assert.Equal("Alice", result.Rows[0][1]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void SelectFromView_with_additional_where()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, category STRING, price REAL)");
            engine.Execute("INSERT INTO products VALUES (1, 'A', 10.0)");
            engine.Execute("INSERT INTO products VALUES (2, 'A', 20.0)");
            engine.Execute("INSERT INTO products VALUES (3, 'B', 15.0)");
            engine.Execute("CREATE VIEW category_a AS SELECT id, price FROM products WHERE category = 'A'");
            var result = engine.Execute("SELECT id FROM category_a WHERE price > 15");
            Assert.Single(result.Rows);
            Assert.Equal(2L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Phase 2: TRUNCATE TABLE ───────────────────────────────────────────────

    [Fact]
    public void TruncateTable_removes_all_rows()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO t VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol')");
            engine.Execute("TRUNCATE TABLE t");
            var result = engine.Execute("SELECT COUNT(*) FROM t");
            Assert.Equal(0L, result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }

    [Fact]
    public void TruncateTable_table_still_usable_after_truncate()
    {
        var (engine, path) = CreateEngine();
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, name STRING)");
            engine.Execute("INSERT INTO t VALUES (1, 'Alice')");
            engine.Execute("TRUNCATE TABLE t");
            engine.Execute("INSERT INTO t VALUES (1, 'Bob')");
            var result = engine.Execute("SELECT name FROM t WHERE id = 1");
            Assert.Single(result.Rows);
            Assert.Equal("Bob", result.Rows[0][0]);
        }
        finally { Cleanup(engine, path); }
    }
}
