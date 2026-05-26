using System.Data;
using System.Data.Common;
using Sqlity.Ado;

namespace Sqlity.Ado.Tests;

public sealed class SqlityConnectionTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

    // ── Connection lifecycle ──────────────────────────────────────────────────

    [Fact]
    public void Open_creates_file_and_state_becomes_open()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            Assert.Equal(ConnectionState.Closed, conn.State);

            conn.Open();

            Assert.Equal(ConnectionState.Open, conn.State);
            Assert.True(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Close_sets_state_to_closed()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            conn.Close();
            Assert.Equal(ConnectionState.Closed, conn.State);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Open_called_twice_does_not_throw()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            conn.Open(); // idempotent
            Assert.Equal(ConnectionState.Open, conn.State);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Database_returns_filename_without_extension()
    {
        using var conn = new SqlityConnection("Data Source=/tmp/mydb.sqlity");
        Assert.Equal("mydb", conn.Database);
    }

    [Fact]
    public void Bare_path_connection_string_is_accepted()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection(path);
            conn.Open();
            Assert.Equal(ConnectionState.Open, conn.State);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

public sealed class SqlityCommandTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

    // ── ExecuteNonQuery ───────────────────────────────────────────────────────

    [Fact]
    public void ExecuteNonQuery_create_table_returns_zero_rows_affected()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            Assert.Equal(0, cmd.ExecuteNonQuery());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExecuteNonQuery_insert_returns_one_rows_affected()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "INSERT INTO t VALUES (1, 'hello');";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExecuteNonQuery_without_open_connection_throws()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY);";
            Assert.Throws<InvalidOperationException>(() => cmd.ExecuteNonQuery());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── ExecuteScalar ─────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteScalar_returns_first_cell_of_first_row()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO t VALUES (42, 'scalar');";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT id FROM t;";
            Assert.Equal(42L, cmd.ExecuteScalar());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExecuteScalar_returns_null_when_no_rows()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT id FROM t;";
            Assert.Null(cmd.ExecuteScalar());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

public sealed class SqlityDataReaderTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

    // ── Full round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void Reader_iterates_rows_with_correct_values()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE users (id INT64 PRIMARY KEY, name STRING);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO users VALUES (1, 'Alice');";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO users VALUES (2, 'Bob');";
                cmd.ExecuteNonQuery();
            }

            using var selectCmd = conn.CreateCommand();
            selectCmd.CommandText = "SELECT id, name FROM users;";
            using var reader = selectCmd.ExecuteReader();

            Assert.True(reader.HasRows);
            Assert.Equal(2, reader.FieldCount);

            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("Alice", reader.GetString(1));

            Assert.True(reader.Read());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal("Bob", reader.GetString(1));

            Assert.False(reader.Read());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_GetName_returns_column_names()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, val FROM t;";
            using var reader = cmd.ExecuteReader();

            Assert.Equal("id",  reader.GetName(0));
            Assert.Equal("val", reader.GetName(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_GetOrdinal_is_case_insensitive()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, val FROM t;";
            using var reader = cmd.ExecuteReader();

            Assert.Equal(0, reader.GetOrdinal("ID"));
            Assert.Equal(0, reader.GetOrdinal("id"));
            Assert.Equal(1, reader.GetOrdinal("VAL"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_HasRows_false_on_empty_table()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id FROM t;";
            using var reader = cmd.ExecuteReader();

            Assert.False(reader.HasRows);
            Assert.False(reader.Read());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_RecordsAffected_reflects_insert()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO t VALUES (1, 'x');";
            using var reader = cmd.ExecuteReader();

            Assert.Equal(1, reader.RecordsAffected);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_indexer_by_name_returns_value()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO t VALUES (7, 'seven');";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, val FROM t;";
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal(7L,      reader["id"]);
            Assert.Equal("seven", reader["val"]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_IsClosed_after_close()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id FROM t;";
            var reader = cmd.ExecuteReader();

            Assert.False(reader.IsClosed);
            reader.Close();
            Assert.True(reader.IsClosed);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Typed getters ─────────────────────────────────────────────────────────

    [Fact]
    public void Reader_GetBoolean_returns_correct_value()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, flag BOOLEAN);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO t VALUES (1, TRUE);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT flag FROM t;";
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

public sealed class SqlityTransactionTests
{
    [Fact]
    public void BeginTransaction_returns_transaction_instance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            var tx = conn.BeginTransaction();
            Assert.NotNull(tx);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Commit_throws_not_supported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            var tx = conn.BeginTransaction();
            Assert.Throws<NotSupportedException>(() => tx.Commit());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
