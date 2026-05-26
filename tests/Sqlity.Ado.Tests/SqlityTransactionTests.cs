using Sqlity.Ado;

namespace Sqlity.Ado.Tests;

public sealed class SqlityTransactionIntegrationTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

    [Fact]
    public void Commit_persists_rows_across_connections()
    {
        var path = TempDb();
        try
        {
            using (var conn = new SqlityConnection($"Data Source={path}"))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "INSERT INTO users VALUES (1, 'Ada');";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }

            using var conn2 = new SqlityConnection($"Data Source={path}");
            conn2.Open();
            using var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = "SELECT name FROM users WHERE id = 1;";
            var name = cmd2.ExecuteScalar();
            Assert.Equal("Ada", name);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Rollback_discards_uncommitted_changes()
    {
        var path = TempDb();
        try
        {
            using (var conn = new SqlityConnection($"Data Source={path}"))
            {
                conn.Open();
                // Create the table without a transaction (auto-commit)
                using (var setup = conn.CreateCommand())
                {
                    setup.CommandText = "CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);";
                    setup.ExecuteNonQuery();
                }

                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO users VALUES (1, 'Ada');";
                    cmd.ExecuteNonQuery();
                }
                tx.Rollback();
            }

            using var conn2 = new SqlityConnection($"Data Source={path}");
            conn2.Open();
            using var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = "SELECT id FROM users;";
            using var reader = cmd2.ExecuteReader();
            Assert.False(reader.Read(), "No rows should exist after rollback.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Dispose_without_commit_auto_rolls_back()
    {
        var path = TempDb();
        try
        {
            using (var conn = new SqlityConnection($"Data Source={path}"))
            {
                conn.Open();
                using (var setup = conn.CreateCommand())
                {
                    setup.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, v INT64 NOT NULL);";
                    setup.ExecuteNonQuery();
                }

                // Transaction is disposed without Commit — should auto-rollback
                using (var tx = conn.BeginTransaction())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT INTO t VALUES (1, 99);";
                    cmd.ExecuteNonQuery();
                    // tx.Dispose() called here, no Commit
                }
            }

            using var conn2 = new SqlityConnection($"Data Source={path}");
            conn2.Open();
            using var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = "SELECT id FROM t;";
            using var reader = cmd2.ExecuteReader();
            Assert.False(reader.Read(), "Insert should have been rolled back on transaction dispose.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BeginTransaction_throws_when_transaction_already_active()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var tx = conn.BeginTransaction();

            Assert.Throws<InvalidOperationException>(() => conn.BeginTransaction());

            tx.Rollback();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".journal")) File.Delete(path + ".journal");
        }
    }

    [Fact]
    public void Commit_after_commit_throws()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            var tx = conn.BeginTransaction();
            tx.Commit();

            Assert.Throws<InvalidOperationException>(() => tx.Commit());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Rollback_after_rollback_throws()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            var tx = conn.BeginTransaction();
            tx.Rollback();

            Assert.Throws<InvalidOperationException>(() => tx.Rollback());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".journal")) File.Delete(path + ".journal");
        }
    }
}
