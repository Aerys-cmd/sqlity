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
