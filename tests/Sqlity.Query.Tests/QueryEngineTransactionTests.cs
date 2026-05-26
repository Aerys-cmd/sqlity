namespace Sqlity.Query.Tests;

public sealed class QueryEngineTransactionTests
{
    [Fact]
    public void Explicit_begin_commit_persists_changes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var engine = new QueryEngine(path))
            {
                engine.Execute("BEGIN;");
                engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64 NOT NULL);");
                engine.Execute("INSERT INTO t VALUES (1, 42);");
                engine.Execute("COMMIT;");
            }

            using var engine2 = new QueryEngine(path);
            var result = engine2.Execute("SELECT v FROM t WHERE id = 1;");
            Assert.Single(result.Rows);
            Assert.Equal(42L, result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Explicit_rollback_discards_changes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var engine = new QueryEngine(path))
            {
                // Create the table and commit it
                engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64 NOT NULL);");
                engine.Execute("INSERT INTO t VALUES (1, 10);");

                // Begin a new transaction, insert, then roll back
                engine.Execute("BEGIN;");
                engine.Execute("INSERT INTO t VALUES (2, 20);");
                engine.Execute("ROLLBACK;");
            }

            using var engine2 = new QueryEngine(path);
            var result = engine2.Execute("SELECT id FROM t;");
            // Only the committed row should exist
            Assert.Single(result.Rows);
            Assert.Equal(1L, result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Auto_commit_applies_to_standalone_statements()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var engine = new QueryEngine(path))
            {
                // Each statement auto-commits, so no explicit BEGIN
                engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64 NOT NULL);");
                engine.Execute("INSERT INTO t VALUES (1, 7);");
            }

            // Re-open to verify the data was committed
            using var engine2 = new QueryEngine(path);
            var result = engine2.Execute("SELECT v FROM t WHERE id = 1;");
            Assert.Single(result.Rows);
            Assert.Equal(7L, result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Nested_begin_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("BEGIN;");
            Assert.Throws<InvalidOperationException>(() => engine.Execute("BEGIN;"));
            engine.Execute("ROLLBACK;");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".journal")) File.Delete(path + ".journal");
        }
    }

    [Fact]
    public void Rollback_without_begin_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var engine = new QueryEngine(path);
            Assert.Throws<InvalidOperationException>(() => engine.Execute("ROLLBACK;"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Dispose_with_active_transaction_rolls_back()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var engine = new QueryEngine(path))
            {
                engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64 NOT NULL);");
                engine.Execute("BEGIN;");
                engine.Execute("INSERT INTO t VALUES (1, 55);");
                // Dispose without COMMIT — should auto-rollback
            }

            Assert.False(File.Exists(path + ".journal"), "Journal should be deleted after rollback on dispose.");

            using var engine2 = new QueryEngine(path);
            var result = engine2.Execute("SELECT id FROM t;");
            Assert.Empty(result.Rows);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".journal")) File.Delete(path + ".journal");
        }
    }

    [Fact]
    public void Auto_rollback_on_failed_dml_leaves_database_consistent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var engine = new QueryEngine(path);
            engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, v INT64 NOT NULL);");
            engine.Execute("INSERT INTO t VALUES (1, 10);");

            // This insert fails (duplicate primary key) — the auto-transaction must roll back
            Assert.Throws<InvalidOperationException>(() =>
                engine.Execute("INSERT INTO t VALUES (1, 99);"));

            // Existing data must be intact
            var result = engine.Execute("SELECT v FROM t WHERE id = 1;");
            Assert.Single(result.Rows);
            Assert.Equal(10L, result.Rows[0][0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
