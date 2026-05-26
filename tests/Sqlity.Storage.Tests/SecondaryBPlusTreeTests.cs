using Sqlity.Storage.BTree;
using Sqlity.Storage.Catalog;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Tests;

public sealed class SecondaryBPlusTreeTests
{
    private static (StorageEngine Storage, string Path) CreateDb(TableSchema schema)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        var storage = StorageEngine.Open(path);
        storage.CreateTable(schema);
        return (storage, path);
    }

    // Helper: build a fake "full row" sized to schema.Columns.Count with value at the given ordinal.
    private static object?[] Row(TableSchema schema, int ordinal, object? value)
    {
        var row = new object?[schema.Columns.Count];
        row[ordinal] = value;
        return row;
    }

    private static TableSchema UsersSchema() => new TableSchema(
        "users",
        new[]
        {
            new ColumnDefinition("id", ColumnType.Int64, IsNullable: false),
            new ColumnDefinition("name", ColumnType.String, IsNullable: true),
            new ColumnDefinition("age", ColumnType.Int64, IsNullable: true)
        },
        primaryKeyOrdinal: 0);

    // ── Unique index ───────────────────────────────────────────────────────────

    [Fact]
    public void CreateIndex_allows_seek_by_indexed_column()
    {
        var (storage, path) = CreateDb(UsersSchema());
        try
        {
            storage.Insert("users", new object?[] { 1L, "Alice", 30L });
            storage.Insert("users", new object?[] { 2L, "Bob", 25L });
            storage.CreateIndex("idx_users_name", "users", new[] { "name" }, isUnique: false);

            var idx = storage.GetIndexesForTable("users").Single(i => i.IndexName == "idx_users_name");
            var schema = storage.GetTable("users").Schema;
            var colDefs = schema.Columns;
            var ordinals = new[] { schema.GetColumnOrdinal("name") };

            var key = IndexKeyEncoder.Encode(colDefs, ordinals, Row(schema, ordinals[0], "Alice"));
            var range = IndexSeekRange.PrefixEquality(key);
            var pks = storage.SeekByIndex(idx, range);

            Assert.Single(pks);
            Assert.Equal(1L, pks[0]);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void UniqueIndex_rejects_duplicate_value()
    {
        var (storage, path) = CreateDb(UsersSchema());
        try
        {
            storage.Insert("users", new object?[] { 1L, "Alice", 30L });
            storage.CreateIndex("idx_unique_name", "users", new[] { "name" }, isUnique: true);

            Assert.Throws<InvalidOperationException>(() =>
                storage.Insert("users", new object?[] { 2L, "Alice", 25L }));
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Delete maintenance ─────────────────────────────────────────────────────

    [Fact]
    public void Delete_removes_index_entry()
    {
        var (storage, path) = CreateDb(UsersSchema());
        try
        {
            storage.Insert("users", new object?[] { 1L, "Alice", 30L });
            storage.Insert("users", new object?[] { 2L, "Bob", 25L });
            storage.CreateIndex("idx_users_name", "users", new[] { "name" }, isUnique: false);

            storage.Delete("users", 1L);

            var idx = storage.GetIndexesForTable("users").Single(i => i.IndexName == "idx_users_name");
            var schema = storage.GetTable("users").Schema;
            var ordinals = new[] { schema.GetColumnOrdinal("name") };
            var key = IndexKeyEncoder.Encode(schema.Columns, ordinals, Row(schema, ordinals[0], "Alice"));
            var pks = storage.SeekByIndex(idx, IndexSeekRange.PrefixEquality(key));

            Assert.Empty(pks);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Multi-row range seek ───────────────────────────────────────────────────

    [Fact]
    public void RangeSeek_returns_all_matching_rows_for_non_unique_index()
    {
        var (storage, path) = CreateDb(UsersSchema());
        try
        {
            storage.Insert("users", new object?[] { 1L, "Alice", 30L });
            storage.Insert("users", new object?[] { 2L, "Alice", 25L });
            storage.Insert("users", new object?[] { 3L, "Bob", 40L });
            storage.CreateIndex("idx_users_name", "users", new[] { "name" }, isUnique: false);

            var idx = storage.GetIndexesForTable("users").Single(i => i.IndexName == "idx_users_name");
            var schema = storage.GetTable("users").Schema;
            var ordinals = new[] { schema.GetColumnOrdinal("name") };
            var key = IndexKeyEncoder.Encode(schema.Columns, ordinals, Row(schema, ordinals[0], "Alice"));
            var pks = storage.SeekByIndex(idx, IndexSeekRange.PrefixEquality(key));

            Assert.Equal(2, pks.Count);
            Assert.Contains(1L, pks);
            Assert.Contains(2L, pks);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Splits (many rows) ────────────────────────────────────────────────────

    [Fact]
    public void Index_handles_many_inserts_and_seeks_correctly()
    {
        var (storage, path) = CreateDb(UsersSchema());
        try
        {
            for (long i = 1; i <= 200; i++)
                storage.Insert("users", new object?[] { i, $"User{i:D4}", 20L + (i % 50) });

            storage.CreateIndex("idx_users_name", "users", new[] { "name" }, isUnique: true);

            var idx = storage.GetIndexesForTable("users").Single(i => i.IndexName == "idx_users_name");
            var schema = storage.GetTable("users").Schema;
            var ordinals = new[] { schema.GetColumnOrdinal("name") };

            // Spot-check a few
            foreach (long pk in new long[] { 1, 50, 100, 200 })
            {
                var key = IndexKeyEncoder.Encode(schema.Columns, ordinals, Row(schema, ordinals[0], $"User{pk:D4}"));
                var pks = storage.SeekByIndex(idx, IndexSeekRange.Equality(key));
                Assert.Single(pks);
                Assert.Equal(pk, pks[0]);
            }
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
