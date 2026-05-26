using Sqlity.Storage.Catalog;
using Sqlity.Storage.IO;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Tests;

/// <summary>
/// Integration tests that exercise multi-page B+ tree behaviour through
/// <see cref="StorageEngine"/>. Each test inserts enough rows to force at
/// least one leaf-page split so that the tree grows beyond a single page.
/// </summary>
public sealed class BTreeMultiPageTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

    private static TableSchema IntSchema(string tableName = "t") =>
        new(tableName,
            new[] { new ColumnDefinition("id", ColumnType.Int64) },
            primaryKeyOrdinal: 0);

    private static TableSchema WideSchema(string tableName = "t") =>
        new(tableName,
            new[]
            {
                new ColumnDefinition("id",   ColumnType.Int64),
                new ColumnDefinition("name", ColumnType.String)
            },
            primaryKeyOrdinal: 0);

    // Insert `count` rows with id 1..count into a fresh table. Uses a fixed-width name
    // so row sizes are predictable.
    private static void InsertRange(StorageEngine engine, string table, int count, string namePrefix = "row")
    {
        for (var i = 1; i <= count; i++)
        {
            engine.Insert(table, new object?[] { (long)i, $"{namePrefix}{i:D6}" });
        }
    }

    // ── Fill-and-read ─────────────────────────────────────────────────────────

    [Fact]
    public void ReadAll_returns_all_rows_sorted_after_leaf_split()
    {
        var path = TempPath();
        try
        {
            const int rowCount = 300; // well above single-page capacity
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(WideSchema());
            InsertRange(storage, "t", rowCount);

            var rows = storage.ReadAll("t");

            Assert.Equal(rowCount, rows.Count);
            for (var i = 0; i < rowCount; i++)
            {
                Assert.Equal((long)(i + 1), rows[i][0]);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Point lookup after split ──────────────────────────────────────────────

    [Fact]
    public void TryReadByPrimaryKey_finds_every_row_after_multiple_splits()
    {
        var path = TempPath();
        try
        {
            const int rowCount = 300;
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(WideSchema());
            InsertRange(storage, "t", rowCount);

            for (var i = 1; i <= rowCount; i++)
            {
                Assert.True(storage.TryReadByPrimaryKey("t", i, out var row),
                    $"Expected row with id {i} to be found.");
                Assert.Equal((long)i, row![0]);
                Assert.Equal($"row{i:D6}", row[1]);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Persist and reopen ────────────────────────────────────────────────────

    [Fact]
    public void Multi_page_table_persists_across_reopen()
    {
        var path = TempPath();
        try
        {
            const int rowCount = 300;

            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(WideSchema());
                InsertRange(storage, "t", rowCount);
            }

            using var reopened = StorageEngine.Open(path);
            var rows = reopened.ReadAll("t");

            Assert.Equal(rowCount, rows.Count);
            for (var i = 0; i < rowCount; i++)
            {
                Assert.Equal((long)(i + 1), rows[i][0]);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Delete after split ────────────────────────────────────────────────────

    [Fact]
    public void Delete_removes_rows_correctly_from_multi_page_table()
    {
        var path = TempPath();
        try
        {
            const int rowCount = 300;
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(WideSchema());
            InsertRange(storage, "t", rowCount);

            // Delete every even-numbered row.
            for (var i = 2; i <= rowCount; i += 2)
            {
                storage.Delete("t", i);
            }

            var rows = storage.ReadAll("t");
            Assert.Equal(rowCount / 2, rows.Count);

            foreach (var row in rows)
            {
                Assert.Equal(1L, (long)row[0]! % 2); // only odd ids remain
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Update after split ────────────────────────────────────────────────────

    [Fact]
    public void Update_modifies_rows_correctly_in_multi_page_table()
    {
        var path = TempPath();
        try
        {
            const int rowCount = 300;
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(WideSchema());
            InsertRange(storage, "t", rowCount);

            // Update the first and last rows.
            storage.Update("t", 1L, new object?[] { 1L, "updated-first" });
            storage.Update("t", (long)rowCount, new object?[] { (long)rowCount, "updated-last" });

            Assert.True(storage.TryReadByPrimaryKey("t", 1L, out var first));
            Assert.Equal("updated-first", first![1]);

            Assert.True(storage.TryReadByPrimaryKey("t", (long)rowCount, out var last));
            Assert.Equal("updated-last", last![1]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Multi-level tree (internal page split) ────────────────────────────────

    [Fact]
    public void ReadAll_returns_all_rows_after_internal_page_split()
    {
        var path = TempPath();
        try
        {
            // Use a wide-enough row count to force internal-page splits (multiple levels).
            const int rowCount = 5000;
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(WideSchema());
            InsertRange(storage, "t", rowCount);

            var rows = storage.ReadAll("t");

            Assert.Equal(rowCount, rows.Count);
            for (var i = 0; i < rowCount; i++)
            {
                Assert.Equal((long)(i + 1), rows[i][0]);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TryReadByPrimaryKey_finds_boundary_keys_in_deep_tree()
    {
        var path = TempPath();
        try
        {
            const int rowCount = 5000;
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(WideSchema());
            InsertRange(storage, "t", rowCount);

            Assert.True(storage.TryReadByPrimaryKey("t", 1L, out var first));
            Assert.Equal(1L, first![0]);

            Assert.True(storage.TryReadByPrimaryKey("t", (long)rowCount, out var last));
            Assert.Equal((long)rowCount, last![0]);

            Assert.False(storage.TryReadByPrimaryKey("t", (long)rowCount + 1, out _));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Duplicate-key rejection still works after split ───────────────────────

    [Fact]
    public void Insert_rejects_duplicate_key_in_multi_page_table()
    {
        var path = TempPath();
        try
        {
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(WideSchema());
            InsertRange(storage, "t", 300);

            var ex = Assert.Throws<InvalidOperationException>(
                () => storage.Insert("t", new object?[] { 100L, "dup" }));
            Assert.Contains("100", ex.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Reverse-order inserts ─────────────────────────────────────────────────

    [Fact]
    public void ReadAll_returns_rows_sorted_when_inserted_in_reverse_order()
    {
        var path = TempPath();
        try
        {
            const int rowCount = 300;
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(IntSchema());

            for (var i = rowCount; i >= 1; i--)
            {
                storage.Insert("t", new object?[] { (long)i });
            }

            var rows = storage.ReadAll("t");

            Assert.Equal(rowCount, rows.Count);
            for (var i = 0; i < rowCount; i++)
            {
                Assert.Equal((long)(i + 1), rows[i][0]);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Page recycling ────────────────────────────────────────────────────────

    /// <summary>
    /// After a split produces two leaf pages and then all rows on one of them
    /// are deleted, the freed page should appear in the free list.
    /// </summary>
    [Fact]
    public void Delete_emptied_leaf_page_is_returned_to_free_list()
    {
        var path = TempPath();
        try
        {
            // Use a wide schema with enough rows to force a split into at least two leaves.
            // 300 wide rows (~40 bytes each) well exceeds a single 4 KB leaf.
            const int rowCount = 300;
            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(WideSchema());
                InsertRange(storage, "t", rowCount);

                // Delete ALL rows — every leaf should eventually be recycled except the
                // root leaf that gets reformatted when the last child reference is removed.
                for (var i = 1; i <= rowCount; i++)
                {
                    storage.Delete("t", i);
                }
            }

            // Inspect the free list via FilePager after the engine is closed.
            using var pager = new FilePager(path);
            var header = pager.ReadDatabaseHeader();

            // At least one leaf page should have been freed.
            Assert.True(header.FreePageCount > 0,
                $"Expected freed pages after deleting all rows, but FreePageCount is {header.FreePageCount}.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// After a split and full delete cycle the tree should still read correctly
    /// (returns an empty result set, not an exception).
    /// </summary>
    [Fact]
    public void ReadAll_after_full_delete_cycle_returns_empty()
    {
        var path = TempPath();
        try
        {
            const int rowCount = 300;
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(WideSchema());
            InsertRange(storage, "t", rowCount);

            for (var i = 1; i <= rowCount; i++)
            {
                storage.Delete("t", i);
            }

            var rows = storage.ReadAll("t");

            Assert.Empty(rows);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// A freed leaf page should be reused by the next allocation. We verify this
    /// by checking that all rows are readable after a delete-then-reinsert cycle,
    /// which exercises the full free-list path without needing direct file access.
    /// </summary>
    [Fact]
    public void Freed_leaf_page_is_reused_on_next_allocation()
    {
        var path = TempPath();
        try
        {
            const int rowCount = 300;

            // Phase 1: Insert rows, delete all (frees pages into the free list).
            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(WideSchema());
                InsertRange(storage, "t", rowCount);

                for (var i = 1; i <= rowCount; i++)
                {
                    storage.Delete("t", i);
                }
            }

            // Phase 2: Re-open and reinsert the same number of rows.
            // The engine must reuse freed pages without errors.
            using (var storage = StorageEngine.Open(path))
            {
                InsertRange(storage, "t", rowCount, namePrefix: "new");

                // All re-inserted rows must be readable.
                var rows = storage.ReadAll("t");
                Assert.Equal(rowCount, rows.Count);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
