using Sqlity.Storage.Catalog;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Tests;

public sealed class IndexCatalogStoreTests
{
    private static (StorageEngine Storage, string Path) CreateDb(TableSchema schema)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        var storage = StorageEngine.Open(path);
        storage.CreateTable(schema);
        return (storage, path);
    }

    private static TableSchema Schema() => new TableSchema(
        "products",
        new[]
        {
            new ColumnDefinition("id", ColumnType.Int64, IsNullable: false),
            new ColumnDefinition("sku", ColumnType.String, IsNullable: false),
            new ColumnDefinition("price", ColumnType.Int64, IsNullable: false)
        },
        primaryKeyOrdinal: 0);

    [Fact]
    public void CreateIndex_persists_and_is_readable_in_same_session()
    {
        var (storage, path) = CreateDb(Schema());
        try
        {
            storage.CreateIndex("idx_sku", "products", new[] { "sku" }, isUnique: true);

            var indexes = storage.GetIndexesForTable("products");
            Assert.Single(indexes);
            Assert.Equal("idx_sku", indexes[0].IndexName);
            Assert.Equal("products", indexes[0].TableName);
            Assert.True(indexes[0].IsUnique);
            Assert.Equal(new[] { "sku" }, indexes[0].Columns);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CreateIndex_persists_across_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(Schema());
                storage.CreateIndex("idx_sku", "products", new[] { "sku" }, isUnique: true);
            }

            using var reopened = StorageEngine.Open(path);
            var indexes = reopened.GetIndexesForTable("products");
            Assert.Single(indexes);
            Assert.Equal("idx_sku", indexes[0].IndexName);
            Assert.True(indexes[0].IsUnique);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CreateIndex_throws_on_duplicate_name()
    {
        var (storage, path) = CreateDb(Schema());
        try
        {
            storage.CreateIndex("idx_sku", "products", new[] { "sku" }, isUnique: false);

            Assert.Throws<InvalidOperationException>(() =>
                storage.CreateIndex("idx_sku", "products", new[] { "price" }, isUnique: false));
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CreateIndex_on_non_existent_column_throws()
    {
        var (storage, path) = CreateDb(Schema());
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                storage.CreateIndex("idx_bogus", "products", new[] { "does_not_exist" }, isUnique: false));
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Multiple_indexes_on_same_table_are_all_returned()
    {
        var (storage, path) = CreateDb(Schema());
        try
        {
            storage.CreateIndex("idx_sku", "products", new[] { "sku" }, isUnique: true);
            storage.CreateIndex("idx_price", "products", new[] { "price" }, isUnique: false);

            var indexes = storage.GetIndexesForTable("products");
            Assert.Equal(2, indexes.Count);
            Assert.Contains(indexes, i => i.IndexName == "idx_sku");
            Assert.Contains(indexes, i => i.IndexName == "idx_price");
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
