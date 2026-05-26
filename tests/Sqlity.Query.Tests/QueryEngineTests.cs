namespace Sqlity.Query.Tests;

public sealed class QueryEngineTests
{
    // ── DELETE ────────────────────────────────────────────────────────────────

    [Fact]
    public void QueryEngine_delete_removes_row_and_select_reflects_deletion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");
            engine.Execute("INSERT INTO users VALUES (2, 'Linus');");

            var deleteResult = engine.Execute("DELETE FROM users WHERE id = 1;");
            var selectResult = engine.Execute("SELECT id, name FROM users;");

            Assert.Equal(1, deleteResult.RowsAffected);
            Assert.Single(selectResult.Rows);
            Assert.Equal(2L, selectResult.Rows[0][0]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void QueryEngine_delete_nonexistent_row_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");

            Assert.Throws<InvalidOperationException>(
                () => engine.Execute("DELETE FROM users WHERE id = 99;"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── UPDATE ────────────────────────────────────────────────────────────────

    [Fact]
    public void QueryEngine_update_modifies_row_and_select_reflects_change()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada', FALSE);");

            var updateResult = engine.Execute("UPDATE users SET name = 'Ada Lovelace', is_active = TRUE WHERE id = 1;");
            var selectResult = engine.Execute("SELECT id, name, is_active FROM users WHERE id = 1;");

            Assert.Equal(1, updateResult.RowsAffected);
            Assert.Single(selectResult.Rows);
            Assert.Equal(1L, selectResult.Rows[0][0]);
            Assert.Equal("Ada Lovelace", selectResult.Rows[0][1]);
            Assert.Equal(true, selectResult.Rows[0][2]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void QueryEngine_update_nonexistent_row_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");

            Assert.Throws<InvalidOperationException>(
                () => engine.Execute("UPDATE users SET name = 'Ghost' WHERE id = 99;"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void QueryEngine_update_primary_key_column_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");

            Assert.Throws<InvalidOperationException>(
                () => engine.Execute("UPDATE users SET id = 2 WHERE id = 1;"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void QueryEngine_update_does_not_affect_other_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");
            engine.Execute("INSERT INTO users VALUES (2, 'Linus');");

            engine.Execute("UPDATE users SET name = 'Ada Lovelace' WHERE id = 1;");

            var allRows = engine.Execute("SELECT id, name FROM users;");
            Assert.Equal(2, allRows.Rows.Count);
            Assert.Equal("Ada Lovelace", allRows.Rows.First(r => (long)r[0]! == 1L)[1]);
            Assert.Equal("Linus", allRows.Rows.First(r => (long)r[0]! == 2L)[1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Existing tests ────────────────────────────────────────────────────────

    [Fact]
    public void QueryEngine_executes_create_insert_and_select_with_primary_key_filter()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var engine = new QueryEngine(path);

            var createResult = engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, is_active BOOLEAN);");
            var firstInsert = engine.Execute("INSERT INTO users VALUES (1, 'Ada', TRUE);");
            var secondInsert = engine.Execute("INSERT INTO users (is_active, name, id) VALUES (FALSE, 'Linus', 2);");
            var selectResult = engine.Execute("SELECT id, name FROM users WHERE id = 2;");

            Assert.Equal(0, createResult.RowsAffected);
            Assert.Equal(1, firstInsert.RowsAffected);
            Assert.Equal(1, secondInsert.RowsAffected);
            Assert.Equal(new[] { "id", "name" }, selectResult.Columns);
            Assert.Collection(
                selectResult.Rows,
                row =>
                {
                    Assert.Equal(2L, row[0]);
                    Assert.Equal("Linus", row[1]);
                });
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void QueryEngine_reopens_persisted_catalog_and_supports_projection_and_blob_literals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using (var engine = new QueryEngine(path))
            {
                engine.Execute("CREATE TABLE files (id INT64 PRIMARY KEY, name STRING, payload BLOB);");
                engine.Execute("INSERT INTO files VALUES (1, 'spec', X'CAFE');");
            }

            using var reopened = new QueryEngine(path);
            var result = reopened.Execute("SELECT name, payload FROM files WHERE id = 1;");

            Assert.Equal(new[] { "name", "payload" }, result.Columns);
            Assert.Collection(
                result.Rows,
                row =>
                {
                    Assert.Equal("spec", row[0]);
                    Assert.Equal(new byte[] { 0xCA, 0xFE }, row[1]);
                });
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

// ── Extended WHERE tests ──────────────────────────────────────────────────────

public sealed class ExtendedWhereTests
{
    private static QueryEngine CreateEngine(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        return new QueryEngine(path);
    }

    // ── Non-PK column filtering ───────────────────────────────────────────────

    [Fact]
    public void Select_where_on_non_pk_string_column_returns_matching_row()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");
            engine.Execute("INSERT INTO users VALUES (2, 'Linus');");
            engine.Execute("INSERT INTO users VALUES (3, 'Ada');");

            var result = engine.Execute("SELECT id, name FROM users WHERE name = 'Ada';");

            Assert.Equal(2, result.Rows.Count);
            Assert.All(result.Rows, row => Assert.Equal("Ada", row[1]));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Select_where_on_non_pk_bool_column_returns_matching_rows()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, active BOOLEAN);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada', TRUE);");
            engine.Execute("INSERT INTO users VALUES (2, 'Linus', FALSE);");
            engine.Execute("INSERT INTO users VALUES (3, 'Grace', TRUE);");

            var result = engine.Execute("SELECT id FROM users WHERE active = TRUE;");

            Assert.Equal(2, result.Rows.Count);
            Assert.Contains(result.Rows, r => (long)r[0]! == 1L);
            Assert.Contains(result.Rows, r => (long)r[0]! == 3L);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Comparison operators ──────────────────────────────────────────────────

    [Fact]
    public void Select_where_not_equals_filters_correctly()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64);");
            engine.Execute("INSERT INTO t VALUES (1, 10);");
            engine.Execute("INSERT INTO t VALUES (2, 20);");
            engine.Execute("INSERT INTO t VALUES (3, 10);");

            var result = engine.Execute("SELECT id FROM t WHERE val <> 10;");

            Assert.Single(result.Rows);
            Assert.Equal(2L, result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Select_where_less_than_filters_correctly()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64);");
            engine.Execute("INSERT INTO t VALUES (1, 50);");
            engine.Execute("INSERT INTO t VALUES (2, 75);");
            engine.Execute("INSERT INTO t VALUES (3, 30);");

            var result = engine.Execute("SELECT id FROM t WHERE score < 60;");

            Assert.Equal(2, result.Rows.Count);
            Assert.Contains(result.Rows, r => (long)r[0]! == 1L);
            Assert.Contains(result.Rows, r => (long)r[0]! == 3L);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Select_where_greater_than_or_equals_filters_correctly()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, score INT64);");
            engine.Execute("INSERT INTO t VALUES (1, 50);");
            engine.Execute("INSERT INTO t VALUES (2, 75);");
            engine.Execute("INSERT INTO t VALUES (3, 75);");

            var result = engine.Execute("SELECT id FROM t WHERE score >= 75;");

            Assert.Equal(2, result.Rows.Count);
            Assert.Contains(result.Rows, r => (long)r[0]! == 2L);
            Assert.Contains(result.Rows, r => (long)r[0]! == 3L);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Select_where_less_than_or_equals_filters_correctly()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64);");
            engine.Execute("INSERT INTO t VALUES (1, 5);");
            engine.Execute("INSERT INTO t VALUES (2, 10);");
            engine.Execute("INSERT INTO t VALUES (3, 15);");

            var result = engine.Execute("SELECT id FROM t WHERE val <= 10;");

            Assert.Equal(2, result.Rows.Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Select_where_greater_than_filters_correctly()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64);");
            engine.Execute("INSERT INTO t VALUES (1, 5);");
            engine.Execute("INSERT INTO t VALUES (2, 10);");
            engine.Execute("INSERT INTO t VALUES (3, 15);");

            var result = engine.Execute("SELECT id FROM t WHERE val > 10;");

            Assert.Single(result.Rows);
            Assert.Equal(3L, result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── AND / OR ──────────────────────────────────────────────────────────────

    [Fact]
    public void Select_where_and_narrows_results()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING, active BOOLEAN);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada', TRUE);");
            engine.Execute("INSERT INTO users VALUES (2, 'Ada', FALSE);");
            engine.Execute("INSERT INTO users VALUES (3, 'Linus', TRUE);");

            var result = engine.Execute("SELECT id FROM users WHERE name = 'Ada' AND active = TRUE;");

            Assert.Single(result.Rows);
            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Select_where_or_broadens_results()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64);");
            engine.Execute("INSERT INTO t VALUES (1, 1);");
            engine.Execute("INSERT INTO t VALUES (2, 5);");
            engine.Execute("INSERT INTO t VALUES (3, 9);");

            var result = engine.Execute("SELECT id FROM t WHERE val = 1 OR val = 9;");

            Assert.Equal(2, result.Rows.Count);
            Assert.Contains(result.Rows, r => (long)r[0]! == 1L);
            Assert.Contains(result.Rows, r => (long)r[0]! == 3L);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Select_where_parentheses_override_precedence()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, a INT64, b INT64);");
            engine.Execute("INSERT INTO t VALUES (1, 1, 0);");
            engine.Execute("INSERT INTO t VALUES (2, 0, 1);");
            engine.Execute("INSERT INTO t VALUES (3, 1, 1);");

            // Without parens: a=1 AND b=0 OR b=1 → (a=1 AND b=0) OR b=1 → rows 1 and 2 and 3
            // With parens: a=1 AND (b=0 OR b=1) → rows 1 and 3
            var result = engine.Execute("SELECT id FROM t WHERE a = 1 AND (b = 0 OR b = 1);");

            Assert.Equal(2, result.Rows.Count);
            Assert.Contains(result.Rows, r => (long)r[0]! == 1L);
            Assert.Contains(result.Rows, r => (long)r[0]! == 3L);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Multi-row DELETE / UPDATE ─────────────────────────────────────────────

    [Fact]
    public void Delete_non_pk_where_removes_multiple_rows_and_reports_correct_count()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, active BOOLEAN);");
            engine.Execute("INSERT INTO users VALUES (1, FALSE);");
            engine.Execute("INSERT INTO users VALUES (2, TRUE);");
            engine.Execute("INSERT INTO users VALUES (3, FALSE);");

            var deleteResult = engine.Execute("DELETE FROM users WHERE active = FALSE;");
            var remaining = engine.Execute("SELECT id FROM users;");

            Assert.Equal(2, deleteResult.RowsAffected);
            Assert.Single(remaining.Rows);
            Assert.Equal(2L, remaining.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Delete_non_pk_where_with_no_match_returns_zero_rows_affected()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");

            var result = engine.Execute("DELETE FROM users WHERE name = 'Ghost';");

            Assert.Equal(0, result.RowsAffected);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Update_non_pk_where_modifies_multiple_rows_and_reports_correct_count()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, active BOOLEAN);");
            engine.Execute("INSERT INTO users VALUES (1, FALSE);");
            engine.Execute("INSERT INTO users VALUES (2, FALSE);");
            engine.Execute("INSERT INTO users VALUES (3, TRUE);");

            var updateResult = engine.Execute("UPDATE users SET active = TRUE WHERE active = FALSE;");
            var all = engine.Execute("SELECT id, active FROM users;");

            Assert.Equal(2, updateResult.RowsAffected);
            Assert.All(all.Rows, row => Assert.Equal(true, row[1]));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

// ── JOIN tests ───────────────────────────────────────────────────────────────

public sealed class JoinTests
{
    private static QueryEngine CreateEngine(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        return new QueryEngine(path);
    }

    // ── INNER JOIN ────────────────────────────────────────────────────────────

    [Fact]
    public void Inner_join_returns_only_matched_rows()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, user_id INT64, amount INT64);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");
            engine.Execute("INSERT INTO users VALUES (2, 'Linus');");
            engine.Execute("INSERT INTO orders VALUES (10, 1, 100);");
            engine.Execute("INSERT INTO orders VALUES (11, 1, 200);");
            // user 2 has no orders

            var result = engine.Execute(
                "SELECT users.name, orders.amount FROM users " +
                "INNER JOIN orders ON users.id = orders.user_id;");

            Assert.Equal(2, result.Rows.Count);
            Assert.All(result.Rows, row => Assert.Equal("Ada", row[0]));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Inner_join_with_no_matches_returns_empty()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, v INT64);");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, a_id INT64);");
            engine.Execute("INSERT INTO a VALUES (1, 10);");
            // no rows in b

            var result = engine.Execute(
                "SELECT a.v FROM a INNER JOIN b ON a.id = b.a_id;");

            Assert.Empty(result.Rows);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Inner_join_select_star_returns_qualified_column_names()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, v INT64);");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, a_id INT64, w INT64);");
            engine.Execute("INSERT INTO a VALUES (1, 10);");
            engine.Execute("INSERT INTO b VALUES (100, 1, 99);");

            var result = engine.Execute("SELECT * FROM a INNER JOIN b ON a.id = b.a_id;");

            Assert.Equal(new[] { "a.id", "a.v", "b.id", "b.a_id", "b.w" }, result.Columns);
            Assert.Single(result.Rows);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── LEFT JOIN ─────────────────────────────────────────────────────────────

    [Fact]
    public void Left_join_includes_unmatched_left_rows_with_null_right_columns()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, user_id INT64, amount INT64);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");
            engine.Execute("INSERT INTO users VALUES (2, 'Linus');");
            engine.Execute("INSERT INTO orders VALUES (10, 1, 100);");

            var result = engine.Execute(
                "SELECT users.name, orders.amount FROM users " +
                "LEFT JOIN orders ON users.id = orders.user_id;");

            Assert.Equal(2, result.Rows.Count);
            var adaRow = result.Rows.First(r => (string)r[0]! == "Ada");
            var linusRow = result.Rows.First(r => (string)r[0]! == "Linus");
            Assert.Equal(100L, adaRow[1]);
            Assert.Null(linusRow[1]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Left_join_with_all_matched_returns_same_as_inner_join()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, user_id INT64);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");
            engine.Execute("INSERT INTO orders VALUES (10, 1);");

            var inner = engine.Execute("SELECT users.name FROM users INNER JOIN orders ON users.id = orders.user_id;");
            var left = engine.Execute("SELECT users.name FROM users LEFT JOIN orders ON users.id = orders.user_id;");

            Assert.Equal(inner.Rows.Count, left.Rows.Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── JOIN + WHERE ──────────────────────────────────────────────────────────

    [Fact]
    public void Join_with_where_filters_combined_rows()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, user_id INT64, amount INT64);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");
            engine.Execute("INSERT INTO users VALUES (2, 'Linus');");
            engine.Execute("INSERT INTO orders VALUES (10, 1, 50);");
            engine.Execute("INSERT INTO orders VALUES (11, 1, 150);");
            engine.Execute("INSERT INTO orders VALUES (12, 2, 75);");

            var result = engine.Execute(
                "SELECT users.name, orders.amount FROM users " +
                "INNER JOIN orders ON users.id = orders.user_id " +
                "WHERE orders.amount > 60;");

            Assert.Equal(2, result.Rows.Count);
            Assert.Contains(result.Rows, r => (long)r[1]! == 150L);
            Assert.Contains(result.Rows, r => (long)r[1]! == 75L);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Left_join_where_on_right_column_excludes_null_rows()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, user_id INT64, amount INT64);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");
            engine.Execute("INSERT INTO users VALUES (2, 'Linus');");
            engine.Execute("INSERT INTO orders VALUES (10, 1, 100);");

            // WHERE on right-side column: unmatched LEFT JOIN rows (null) don't pass comparison
            var result = engine.Execute(
                "SELECT users.name FROM users " +
                "LEFT JOIN orders ON users.id = orders.user_id " +
                "WHERE orders.amount > 0;");

            Assert.Single(result.Rows);
            Assert.Equal("Ada", result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Qualified column references ────────────────────────────────────────────

    [Fact]
    public void Select_with_qualified_column_reference_in_single_table_query()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");

            var result = engine.Execute("SELECT users.id, users.name FROM users WHERE users.id = 1;");

            Assert.Equal(new[] { "users.id", "users.name" }, result.Columns);
            Assert.Single(result.Rows);
            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Join_ambiguous_unqualified_column_throws()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, v INT64);");
            engine.Execute("CREATE TABLE b (id INT64 PRIMARY KEY, a_id INT64);");
            engine.Execute("INSERT INTO a VALUES (1, 10);");
            engine.Execute("INSERT INTO b VALUES (100, 1);");

            // 'id' exists in both a and b — should throw
            Assert.Throws<InvalidOperationException>(
                () => engine.Execute("SELECT id FROM a INNER JOIN b ON a.id = b.a_id;"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Join_same_table_twice_throws()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE a (id INT64 PRIMARY KEY, v INT64);");
            engine.Execute("INSERT INTO a VALUES (1, 10);");

            Assert.Throws<InvalidOperationException>(
                () => engine.Execute("SELECT a.v FROM a INNER JOIN a ON a.id = a.id;"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── JOIN keyword alone defaults to INNER ────────────────────────────────────

    [Fact]
    public void Join_without_inner_keyword_defaults_to_inner_join()
    {
        using var engine = CreateEngine(out var path);
        try
        {
            engine.Execute("CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);");
            engine.Execute("CREATE TABLE orders (id INT64 PRIMARY KEY, user_id INT64);");
            engine.Execute("INSERT INTO users VALUES (1, 'Ada');");
            engine.Execute("INSERT INTO users VALUES (2, 'Linus');");
            engine.Execute("INSERT INTO orders VALUES (10, 1);");

            var result = engine.Execute(
                "SELECT users.name FROM users JOIN orders ON users.id = orders.user_id;");

            // Only Ada has an order
            Assert.Single(result.Rows);
            Assert.Equal("Ada", result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
