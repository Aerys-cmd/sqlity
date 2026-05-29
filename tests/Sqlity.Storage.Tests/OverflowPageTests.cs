using Sqlity.Storage.BTree;
using Sqlity.Storage.Catalog;
using Sqlity.Storage.IO;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Tests;

/// <summary>Tests for row-level overflow pages (payloads that exceed a single leaf page).</summary>
public sealed class OverflowPageTests
{
    // The usable inline payload capacity for a cell on a 4096-byte page.
    // PageSize(4096) - PageHeader(20) - CellPointer(2) - LeafCellHeader(10) = 4064.
    // But after RowSerializer adds a type-byte per column, the Blob column header overhead
    // means we can use a slightly smaller value as a safe "below limit" boundary.
    // We use the B+ tree level constant directly in the overflow scenarios.

    // ─── Schema helpers ──────────────────────────────────────────────────────────

    private static TableSchema BlobSchema(string name = "t") =>
        new(name,
            [new ColumnDefinition("id", ColumnType.Int64), new ColumnDefinition("data", ColumnType.Blob)],
            primaryKeyOrdinal: 0);

    private static byte[] MakeBlob(int size, byte fill = 0xAB)
    {
        var b = new byte[size];
        b.AsSpan().Fill(fill);
        return b;
    }

    // ─── Inline round-trip (sanity) ──────────────────────────────────────────────

    [Fact]
    public void Insert_and_read_small_blob_stays_inline()
    {
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        var blob = MakeBlob(100, 0x11);
        storage.Insert("t", new object?[] { 1L, blob });

        var rows = storage.ReadAll("t");
        Assert.Single(rows);
        Assert.Equal(blob, (byte[])rows[0][1]!);
    }

    // ─── Single overflow page ─────────────────────────────────────────────────────

    [Fact]
    public void Insert_and_read_blob_exceeding_inline_limit()
    {
        // Blob of 8000 bytes will definitely require overflow pages.
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        var blob = MakeBlob(8000, 0x22);
        storage.Insert("t", new object?[] { 1L, blob });

        var rows = storage.ReadAll("t");
        Assert.Single(rows);
        Assert.Equal(blob, (byte[])rows[0][1]!);
    }

    // ─── Multi-page overflow chain ───────────────────────────────────────────────

    [Fact]
    public void Insert_and_read_large_blob_spanning_multiple_overflow_pages()
    {
        // ~3 full overflow pages (each 4076 B data, total ~12 500 bytes).
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        var blob = MakeBlob(12_500, 0x5A);
        storage.Insert("t", new object?[] { 1L, blob });

        var rows = storage.ReadAll("t");
        Assert.Single(rows);
        Assert.Equal(blob, (byte[])rows[0][1]!);
    }

    // ─── ReadAll materialises all types ──────────────────────────────────────────

    [Fact]
    public void ReadAll_returns_mix_of_inline_and_overflow_rows_correctly()
    {
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        var small = MakeBlob(100, 0x01);
        var large = MakeBlob(9000, 0x02);
        var medium = MakeBlob(200, 0x03);

        storage.Insert("t", new object?[] { 1L, small });
        storage.Insert("t", new object?[] { 2L, large });
        storage.Insert("t", new object?[] { 3L, medium });

        var rows = storage.ReadAll("t");
        Assert.Equal(3, rows.Count);
        Assert.Equal(small, (byte[])rows[0][1]!);
        Assert.Equal(large, (byte[])rows[1][1]!);
        Assert.Equal(medium, (byte[])rows[2][1]!);
    }

    // ─── TryReadByPrimaryKey ─────────────────────────────────────────────────────

    [Fact]
    public void TryReadByPrimaryKey_materialises_overflow_row()
    {
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        var blob = MakeBlob(9000, 0x33);
        storage.Insert("t", new object?[] { 42L, blob });

        var found = storage.TryReadByPrimaryKey("t", 42L, out var values);
        Assert.True(found);
        Assert.Equal(blob, (byte[])values![1]!);
    }

    // ─── Delete ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_overflow_row_leaves_table_empty()
    {
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        var blob = MakeBlob(9000, 0x44);
        storage.Insert("t", new object?[] { 1L, blob });
        storage.Delete("t", 1L);

        Assert.Empty(storage.ReadAll("t"));
    }

    // ─── Update ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Update_inline_to_overflow()
    {
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        var small = MakeBlob(50, 0xAA);
        storage.Insert("t", new object?[] { 1L, small });

        var large = MakeBlob(9000, 0xBB);
        storage.Update("t", 1L, new object?[] { 1L, large });

        var rows = storage.ReadAll("t");
        Assert.Single(rows);
        Assert.Equal(large, (byte[])rows[0][1]!);
    }

    [Fact]
    public void Update_overflow_to_inline()
    {
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        var large = MakeBlob(9000, 0xCC);
        storage.Insert("t", new object?[] { 1L, large });

        var small = MakeBlob(50, 0xDD);
        storage.Update("t", 1L, new object?[] { 1L, small });

        var rows = storage.ReadAll("t");
        Assert.Single(rows);
        Assert.Equal(small, (byte[])rows[0][1]!);
    }

    [Fact]
    public void Update_overflow_to_different_overflow()
    {
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        var v1 = MakeBlob(9000, 0xEE);
        storage.Insert("t", new object?[] { 1L, v1 });

        var v2 = MakeBlob(15_000, 0xFF);
        storage.Update("t", 1L, new object?[] { 1L, v2 });

        var rows = storage.ReadAll("t");
        Assert.Single(rows);
        Assert.Equal(v2, (byte[])rows[0][1]!);
    }

    // ─── Persistence ────────────────────────────────────────────────────────────

    [Fact]
    public void Overflow_rows_persist_across_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"overflow_persist_{Guid.NewGuid():N}.db");
        try
        {
            var blob = MakeBlob(10_000, 0x7E);

            using (var s = StorageEngine.Open(path))
            {
                s.BeginTransaction();
                s.CreateTable(BlobSchema());
                s.Insert("t", new object?[] { 1L, blob });
                s.Commit();
            }

            using (var s = StorageEngine.Open(path))
            {
                var rows = s.ReadAll("t");
                Assert.Single(rows);
                Assert.Equal(blob, (byte[])rows[0][1]!);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ─── Direct BPlusTree diagnostics ───────────────────────────────────────────

    [Fact]
    public void BPlusTree_Insert_large_payload_materializes_via_overflow()
    {
        // Build the state directly without going through StorageEngine serialization.
        var pager = new InMemoryPager();
        pager.InitializeNew();

        // Allocate a single table-leaf root page (page 1).
        var rootPageId = pager.AllocatePage(Sqlity.Storage.Pages.PageType.TableLeaf);

        // Build an 8016-byte payload manually (simulates RowSerializer output).
        var payload = new byte[8016];
        payload.AsSpan().Fill(0x22);

        var cell = new TableLeafCell(1L, payload);
        var tree = new BPlusTree(pager, rootPageId);
        tree.Insert(cell);

        var cells = tree.ReadAll();
        Assert.Single(cells);
        var result = cells[0];
        Assert.False(result.IsOverflowPointer, "MaterializeCell should have resolved the pointer.");
        Assert.Equal(8016, result.Payload.Length);
    }

    [Fact]
    public void BPlusTree_Insert_writes_overflow_pointer_cell_with_correct_raw_bytes()
    {
        var pager = new InMemoryPager();
        pager.InitializeNew();

        var rootPageId = pager.AllocatePage(Sqlity.Storage.Pages.PageType.TableLeaf);
        Assert.Equal(1u, rootPageId);

        var payload = new byte[8016];
        payload.AsSpan().Fill(0x22);

        var cell = new TableLeafCell(1L, payload);
        var tree = new BPlusTree(pager, rootPageId);
        tree.Insert(cell);

        // After insert, read root page directly and check raw bytes.
        var page = pager.ReadPage(rootPageId);
        var hdr = page.ReadHeader();

        Assert.Equal(Sqlity.Storage.Pages.PageType.TableLeaf, hdr.PageType);
        Assert.Equal(1, hdr.CellCount);

        // The cell pointer array starts at byte 20 (PageHeader.Size).
        // Pointer[0] should point to where the cell data starts.
        var cellOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            page.ReadOnlySpan[20..22]);

        Assert.Equal(4078, cellOffset); // expected: 4096 - 18 = 4078

        // Check 8 bytes starting at cellOffset (key bytes)
        var keyBytes = page.ReadOnlySpan[cellOffset..(cellOffset + 8)].ToArray();
        var expectedKey = new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        Assert.Equal(expectedKey, keyBytes);

        // The 18-byte pointer cell layout: [key:8][sentinel 0xFFFF:2][totalSize:4][firstPageId:4]
        var sentinel = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            page.ReadOnlySpan[(cellOffset + 8)..(cellOffset + 10)]);
        var totalSizeInPage = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            page.ReadOnlySpan[(cellOffset + 10)..(cellOffset + 14)]);
        var firstPageId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            page.ReadOnlySpan[(cellOffset + 14)..(cellOffset + 18)]);

        // Dump raw bytes for diagnosis
        var rawBytes = page.ReadOnlySpan[cellOffset..(cellOffset + 18)].ToArray();
        var rawHex = string.Join(" ", rawBytes.Select(b => b.ToString("X2")));
        Assert.Equal(0xFFFF, sentinel);
        Assert.True(totalSizeInPage == 8016u, $"totalSize={totalSizeInPage}, rawHex={rawHex}");
        Assert.NotEqual(0u, firstPageId);
    }

    [Fact]
    public void TableLeafCell_WriteTo_encodes_overflow_pointer_payload_correctly()
    {
        // Verify TableLeafCell.WriteTo puts payload bytes at the right offset.
        var pointerPayload = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pointerPayload.AsSpan(0, 4), 8016u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pointerPayload.AsSpan(4, 4), 2u);

        var cell = new TableLeafCell(1L, pointerPayload) { IsOverflowPointer = true };
        var dest = new byte[18];
        cell.WriteTo(dest);

        // bytes 10..18 should be the pointer payload
        var writtenPayload = dest[10..18];
        Assert.Equal(pointerPayload, writtenPayload);

        // Also verify sentinel is at bytes 8-9
        var sentinel = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(dest.AsSpan(8, 2));
        Assert.Equal(0xFFFF, sentinel);

        var totalSizeRead = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dest.AsSpan(10, 4));
        Assert.Equal(8016u, totalSizeRead);
    }

    // ─── Drop table ─────────────────────────────────────────────────────────────

    [Fact]
    public void Drop_table_with_overflow_rows_does_not_throw()
    {
        using var storage = StorageEngine.Open(":memory:");
        storage.CreateTable(BlobSchema());

        for (int i = 1; i <= 3; i++)
            storage.Insert("t", new object?[] { (long)i, MakeBlob(9000 + i) });

        // DropTable must walk and release overflow chains without throwing.
        storage.DropTable("t");

        // Table no longer exists.
        Assert.Throws<InvalidOperationException>(() => storage.ReadAll("t"));
    }
}
