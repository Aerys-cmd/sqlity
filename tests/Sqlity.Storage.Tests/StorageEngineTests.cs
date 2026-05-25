using Sqlity.Storage.Catalog;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Tests;

public sealed class StorageEngineTests
{
    [Fact]
    public void StorageEngine_delete_removes_row_and_read_returns_false()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(new TableSchema(
                "users",
                new[]
                {
                    new ColumnDefinition("id", ColumnType.Int64),
                    new ColumnDefinition("name", ColumnType.String)
                },
                primaryKeyOrdinal: 0));

            storage.Insert("users", new object?[] { 1L, "Ada" });
            storage.Insert("users", new object?[] { 2L, "Linus" });

            storage.Delete("users", 1L);

            var rows = storage.ReadAll("users");
            Assert.Single(rows);
            Assert.Equal(2L, rows[0][0]);

            Assert.False(storage.TryReadByPrimaryKey("users", 1L, out _));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void StorageEngine_delete_throws_for_nonexistent_primary_key()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(new TableSchema(
                "users",
                new[] { new ColumnDefinition("id", ColumnType.Int64) },
                primaryKeyOrdinal: 0));

            var ex = Assert.Throws<InvalidOperationException>(() => storage.Delete("users", 99L));
            Assert.Contains("99", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void StorageEngine_update_modifies_row_and_persists_across_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(new TableSchema(
                    "users",
                    new[]
                    {
                        new ColumnDefinition("id", ColumnType.Int64),
                        new ColumnDefinition("name", ColumnType.String),
                        new ColumnDefinition("is_active", ColumnType.Boolean)
                    },
                    primaryKeyOrdinal: 0));

                storage.Insert("users", new object?[] { 1L, "Ada", false });
                storage.Update("users", 1L, new object?[] { 1L, "Ada Lovelace", true });
            }

            using var reopened = StorageEngine.Open(path);
            Assert.True(reopened.TryReadByPrimaryKey("users", 1L, out var row));
            Assert.Equal("Ada Lovelace", row![1]);
            Assert.Equal(true, row![2]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void StorageEngine_update_throws_for_nonexistent_primary_key()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(new TableSchema(
                "users",
                new[]
                {
                    new ColumnDefinition("id", ColumnType.Int64),
                    new ColumnDefinition("name", ColumnType.String)
                },
                primaryKeyOrdinal: 0));

            var ex = Assert.Throws<InvalidOperationException>(
                () => storage.Update("users", 99L, new object?[] { 99L, "Ghost" }));
            Assert.Contains("99", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void StorageEngine_delete_then_reinsert_same_key_succeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(new TableSchema(
                "users",
                new[]
                {
                    new ColumnDefinition("id", ColumnType.Int64),
                    new ColumnDefinition("name", ColumnType.String)
                },
                primaryKeyOrdinal: 0));

            storage.Insert("users", new object?[] { 1L, "Ada" });
            storage.Delete("users", 1L);
            storage.Insert("users", new object?[] { 1L, "Ada v2" });

            Assert.True(storage.TryReadByPrimaryKey("users", 1L, out var row));
            Assert.Equal("Ada v2", row![1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Existing tests ─────────────────────────────────────────────────────────

    [Fact]
    public void StorageEngine_persists_tables_and_rows_across_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(
                    new TableSchema(
                        "users",
                        new[]
                        {
                            new ColumnDefinition("id", ColumnType.Int64),
                            new ColumnDefinition("name", ColumnType.String),
                            new ColumnDefinition("is_active", ColumnType.Boolean)
                        },
                        primaryKeyOrdinal: 0));

                storage.Insert("users", new object?[] { 1L, "Ada", true });
                storage.Insert("users", new object?[] { 2L, "Linus", false });
            }

            using var reopened = StorageEngine.Open(path);
            var tables = reopened.ListTables();
            var rows = reopened.ReadAll("users");

            Assert.Collection(
                tables,
                table =>
                {
                    Assert.Equal("users", table.TableName);
                    Assert.Equal<uint>(2, table.RootPageId);
                    Assert.Equal("id", table.Schema.PrimaryKeyColumn.Name);
                });

            Assert.Collection(
                rows,
                row =>
                {
                    Assert.Equal(1L, row[0]);
                    Assert.Equal("Ada", row[1]);
                    Assert.Equal(true, row[2]);
                },
                row =>
                {
                    Assert.Equal(2L, row[0]);
                    Assert.Equal("Linus", row[1]);
                    Assert.Equal(false, row[2]);
                });

            Assert.True(reopened.TryReadByPrimaryKey("users", 2, out var rowById));
            Assert.NotNull(rowById);
            Assert.Equal("Linus", rowById![1]);
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
    public void StorageEngine_rejects_duplicate_table_names()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var storage = StorageEngine.Open(path);
            var schema = new TableSchema(
                "users",
                new[]
                {
                    new ColumnDefinition("id", ColumnType.Int64),
                    new ColumnDefinition("name", ColumnType.String)
                },
                primaryKeyOrdinal: 0);

            var first = storage.CreateTable(schema);
            var duplicate = Assert.Throws<InvalidOperationException>(() => storage.CreateTable(schema));

            Assert.IsType<TableInfo>(first);
            Assert.Contains("already exists", duplicate.Message);
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
