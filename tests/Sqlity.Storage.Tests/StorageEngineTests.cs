using Sqlity.Storage.Catalog;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Tests;

public sealed class StorageEngineTests
{
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
