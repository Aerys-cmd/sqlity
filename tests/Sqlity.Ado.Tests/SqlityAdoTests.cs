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

    [Fact]
    public void Reader_GetFieldType_returns_schema_type_even_when_first_row_is_null()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, name STRING);";
            cmd.ExecuteNonQuery();
            // Insert with NULL name — first (and only) row has null for 'name'
            cmd.CommandText = "INSERT INTO t (id) VALUES (1);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, name FROM t;";
            using var reader = cmd.ExecuteReader();

            Assert.Equal(typeof(long),   reader.GetFieldType(0));
            Assert.Equal(typeof(string), reader.GetFieldType(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_GetFieldType_returns_schema_type_on_empty_result_set()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, flag BOOLEAN);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, flag FROM t;";
            using var reader = cmd.ExecuteReader();

            Assert.False(reader.HasRows);
            Assert.Equal(typeof(long),   reader.GetFieldType(0));
            Assert.Equal(typeof(bool),   reader.GetFieldType(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_IsDBNull_true_for_nullable_column_with_null_value()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO t (id) VALUES (10);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, val FROM t;";
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read());
            Assert.False(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(DBNull.Value, reader.GetValue(1));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_GetFieldType_preserves_types_across_join_columns()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE orders (id INT64 PRIMARY KEY, amount INT64);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE TABLE flags (id INT64 PRIMARY KEY, active BOOLEAN);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO orders VALUES (1, 100);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO flags VALUES (1, TRUE);";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT orders.id, orders.amount, flags.active FROM orders JOIN flags ON orders.id = flags.id;";
            using var reader = cmd.ExecuteReader();

            Assert.Equal(typeof(long),   reader.GetFieldType(0));
            Assert.Equal(typeof(long),   reader.GetFieldType(1));
            Assert.Equal(typeof(bool),   reader.GetFieldType(2));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

public sealed class SqlityDataReaderSchemaTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

    [Fact]
    public void GetSchemaTable_returns_correct_name_ordinal_and_type()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, name STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, name FROM t;";
            using var reader = cmd.ExecuteReader();

            var schema = reader.GetSchemaTable();

            Assert.NotNull(schema);
            Assert.Equal(2, schema.Rows.Count);

            Assert.Equal("id",          schema.Rows[0]["ColumnName"]);
            Assert.Equal(0,             schema.Rows[0]["ColumnOrdinal"]);
            Assert.Equal(typeof(long),  schema.Rows[0]["DataType"]);

            Assert.Equal("name",         schema.Rows[1]["ColumnName"]);
            Assert.Equal(1,              schema.Rows[1]["ColumnOrdinal"]);
            Assert.Equal(typeof(string), schema.Rows[1]["DataType"]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetSchemaTable_AllowDBNull_false_for_primary_key_and_not_null_columns()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val INT64 NOT NULL, opt STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, val, opt FROM t;";
            using var reader = cmd.ExecuteReader();

            var schema = reader.GetSchemaTable();

            Assert.Equal(false, schema!.Rows[0]["AllowDBNull"]); // PRIMARY KEY
            Assert.Equal(false, schema.Rows[1]["AllowDBNull"]); // NOT NULL
            Assert.Equal(true,  schema.Rows[2]["AllowDBNull"]); // nullable
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetSchemaTable_AllowDBNull_true_for_nullable_column()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, note STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT note FROM t;";
            using var reader = cmd.ExecuteReader();

            var schema = reader.GetSchemaTable();

            Assert.Equal(true, schema!.Rows[0]["AllowDBNull"]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetSchemaTable_works_on_empty_result_set()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val BOOLEAN NOT NULL);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT id, val FROM t;";
            using var reader = cmd.ExecuteReader();

            Assert.False(reader.HasRows);
            var schema = reader.GetSchemaTable();

            Assert.NotNull(schema);
            Assert.Equal(2, schema.Rows.Count);
            Assert.Equal("id",        schema.Rows[0]["ColumnName"]);
            Assert.Equal(typeof(long), schema.Rows[0]["DataType"]);
            Assert.Equal(false,       schema.Rows[0]["AllowDBNull"]);
            Assert.Equal(typeof(bool), schema.Rows[1]["DataType"]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetSchemaTable_AllowDBNull_true_for_join_nullable_columns()
    {
        var path = TempDb();
        try
        {
            using var conn = new SqlityConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE a (id INT64 PRIMARY KEY, x STRING);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE TABLE b (id INT64 PRIMARY KEY, y INT64 NOT NULL);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT a.id, a.x, b.y FROM a JOIN b ON a.id = b.id;";
            using var reader = cmd.ExecuteReader();

            var schema = reader.GetSchemaTable();

            Assert.Equal(false, schema!.Rows[0]["AllowDBNull"]); // a.id — PRIMARY KEY
            Assert.Equal(true,  schema.Rows[1]["AllowDBNull"]); // a.x — nullable
            Assert.Equal(false, schema.Rows[2]["AllowDBNull"]); // b.y — NOT NULL
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
    public void Commit_commits_transaction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var conn = new SqlityConnection($"Data Source={path}"))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, v INT64 NOT NULL);";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "INSERT INTO t VALUES (1, 99);";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }

            // Re-open to verify durability
            using var conn2 = new SqlityConnection($"Data Source={path}");
            conn2.Open();
            using var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = "SELECT v FROM t WHERE id = 1;";
            var result = cmd2.ExecuteScalar();
            Assert.Equal(99L, result);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

public sealed class SqlityAsyncTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

    [Fact]
    public async Task OpenAsync_opens_connection()
    {
        var path = TempDb();
        try
        {
            await using var conn = new SqlityConnection($"Data Source={path}");
            await conn.OpenAsync();
            Assert.Equal(ConnectionState.Open, conn.State);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CloseAsync_closes_connection()
    {
        var path = TempDb();
        try
        {
            await using var conn = new SqlityConnection($"Data Source={path}");
            await conn.OpenAsync();
            await conn.CloseAsync();
            Assert.Equal(ConnectionState.Closed, conn.State);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_executes_ddl_and_returns_rows_affected()
    {
        var path = TempDb();
        try
        {
            await using var conn = new SqlityConnection($"Data Source={path}");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val STRING);";
            var rows = await cmd.ExecuteNonQueryAsync();
            Assert.Equal(0, rows);

            cmd.CommandText = "INSERT INTO t VALUES (1, 'hello');";
            rows = await cmd.ExecuteNonQueryAsync();
            Assert.Equal(1, rows);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExecuteScalarAsync_returns_first_cell()
    {
        var path = TempDb();
        try
        {
            await using var conn = new SqlityConnection($"Data Source={path}");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, val INT64);";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "INSERT INTO t VALUES (1, 42);";
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "SELECT val FROM t WHERE id = 1;";
            var result = await cmd.ExecuteScalarAsync();
            Assert.Equal(42L, result);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExecuteReaderAsync_and_ReadAsync_iterate_rows()
    {
        var path = TempDb();
        try
        {
            await using var conn = new SqlityConnection($"Data Source={path}");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY, name STRING);";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "INSERT INTO t VALUES (1, 'Alice'), (2, 'Bob');";
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "SELECT id, name FROM t ORDER BY id;";
            await using var reader = await cmd.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("Alice", reader.GetString(1));

            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal("Bob", reader.GetString(1));

            Assert.False(await reader.ReadAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task NextResultAsync_returns_false()
    {
        var path = TempDb();
        try
        {
            await using var conn = new SqlityConnection($"Data Source={path}");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INT64 PRIMARY KEY); INSERT INTO t VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "SELECT id FROM t;";
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.False(await reader.NextResultAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CancellationToken_cancelled_before_call_throws_OperationCanceledException()
    {
        var path = TempDb();
        try
        {
            await using var conn = new SqlityConnection($"Data Source={path}");
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => conn.OpenAsync(cts.Token));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
