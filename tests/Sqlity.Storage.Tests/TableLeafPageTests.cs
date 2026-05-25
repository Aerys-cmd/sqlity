using Sqlity.Storage.BTree;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.Tests;

public sealed class TableLeafPageTests
{
    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public void TryDelete_removes_middle_cell_and_maintains_sort_order()
    {
        var page = TableLeafPage.Create(1);
        page.TryInsert(new TableLeafCell(10, new byte[] { 0x10 }));
        page.TryInsert(new TableLeafCell(20, new byte[] { 0x20 }));
        page.TryInsert(new TableLeafCell(30, new byte[] { 0x30 }));

        var status = page.TryDelete(20);
        var cells = page.ReadAllCells();

        Assert.Equal(TableLeafDeleteStatus.Success, status);
        Assert.Equal(2, cells.Count);
        Assert.Equal(10, cells[0].PrimaryKey);
        Assert.Equal(30, cells[1].PrimaryKey);
        Assert.Equal(new byte[] { 0x10 }, cells[0].Payload);
        Assert.Equal(new byte[] { 0x30 }, cells[1].Payload);
    }

    [Fact]
    public void TryDelete_removes_only_cell()
    {
        var page = TableLeafPage.Create(1);
        page.TryInsert(new TableLeafCell(42, new byte[] { 0x42 }));

        var status = page.TryDelete(42);

        Assert.Equal(TableLeafDeleteStatus.Success, status);
        Assert.Equal(0, page.CellCount);
    }

    [Fact]
    public void TryDelete_removes_cell_at_CellContentStart_physical_position()
    {
        var page = TableLeafPage.Create(1);
        // Insert order determines physical position; key=5 is inserted last → sits at CellContentStart
        page.TryInsert(new TableLeafCell(20, new byte[] { 0x20 }));
        page.TryInsert(new TableLeafCell(5, new byte[] { 0x05 }));

        var status = page.TryDelete(5);
        var cells = page.ReadAllCells();

        Assert.Equal(TableLeafDeleteStatus.Success, status);
        Assert.Single(cells);
        Assert.Equal(20, cells[0].PrimaryKey);
        Assert.Equal(new byte[] { 0x20 }, cells[0].Payload);
    }

    [Fact]
    public void TryDelete_returns_not_found_for_missing_key()
    {
        var page = TableLeafPage.Create(1);
        page.TryInsert(new TableLeafCell(1, new byte[] { 0x01 }));

        var status = page.TryDelete(99);

        Assert.Equal(TableLeafDeleteStatus.NotFound, status);
        Assert.Equal(1, page.CellCount);
    }

    [Fact]
    public void TryDelete_increases_free_space_by_cell_size_plus_pointer()
    {
        var page = TableLeafPage.Create(1);
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        page.TryInsert(new TableLeafCell(1, payload));
        var freeBefore = page.FreeSpace;

        page.TryDelete(1);

        var expectedReclaimed = new TableLeafCell(1, payload).GetRequiredSize() + BTreePageLayout.CellPointerSize;
        Assert.Equal(freeBefore + expectedReclaimed, page.FreeSpace);
    }

    [Fact]
    public void TryDelete_and_reinsert_same_key_succeeds()
    {
        var page = TableLeafPage.Create(1);
        page.TryInsert(new TableLeafCell(7, new byte[] { 0x07 }));
        page.TryDelete(7);

        var reinsert = page.TryInsert(new TableLeafCell(7, new byte[] { 0x77 }));

        Assert.Equal(TableLeafInsertStatus.Success, reinsert);
        Assert.True(page.TryGetCell(7, out var cell));
        Assert.Equal(new byte[] { 0x77 }, cell.Payload);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    [Fact]
    public void TryUpdate_replaces_payload_same_size_in_place()
    {
        var page = TableLeafPage.Create(1);
        page.TryInsert(new TableLeafCell(1, new byte[] { 0x01 }));
        var freeBefore = page.FreeSpace;

        var status = page.TryUpdate(new TableLeafCell(1, new byte[] { 0xFF }));

        Assert.Equal(TableLeafUpdateStatus.Success, status);
        Assert.True(page.TryGetCell(1, out var cell));
        Assert.Equal(new byte[] { 0xFF }, cell.Payload);
        Assert.Equal(freeBefore, page.FreeSpace); // same size → no space change
    }

    [Fact]
    public void TryUpdate_replaces_payload_with_larger_value()
    {
        var page = TableLeafPage.Create(1);
        page.TryInsert(new TableLeafCell(5, new byte[] { 0x05 }));
        page.TryInsert(new TableLeafCell(10, new byte[] { 0x10 }));
        page.TryInsert(new TableLeafCell(20, new byte[] { 0x20 }));

        var status = page.TryUpdate(new TableLeafCell(10, new byte[] { 0xAA, 0xBB, 0xCC }));

        Assert.Equal(TableLeafUpdateStatus.Success, status);
        var cells = page.ReadAllCells();
        Assert.Equal(3, cells.Count);
        Assert.Equal(5, cells[0].PrimaryKey);
        Assert.Equal(10, cells[1].PrimaryKey);
        Assert.Equal(20, cells[2].PrimaryKey);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, cells[1].Payload);
        Assert.Equal(new byte[] { 0x05 }, cells[0].Payload);
        Assert.Equal(new byte[] { 0x20 }, cells[2].Payload);
    }

    [Fact]
    public void TryUpdate_replaces_payload_with_smaller_value()
    {
        var page = TableLeafPage.Create(1);
        page.TryInsert(new TableLeafCell(3, new byte[] { 0xAA, 0xBB, 0xCC }));
        page.TryInsert(new TableLeafCell(7, new byte[] { 0x11, 0x22, 0x33 }));

        var status = page.TryUpdate(new TableLeafCell(3, new byte[] { 0xFF }));

        Assert.Equal(TableLeafUpdateStatus.Success, status);
        var cells = page.ReadAllCells();
        Assert.Equal(2, cells.Count);
        Assert.Equal(new byte[] { 0xFF }, cells[0].Payload);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, cells[1].Payload);
    }

    [Fact]
    public void TryUpdate_returns_not_found_for_missing_key()
    {
        var page = TableLeafPage.Create(1);
        page.TryInsert(new TableLeafCell(1, new byte[] { 0x01 }));

        var status = page.TryUpdate(new TableLeafCell(99, new byte[] { 0xFF }));

        Assert.Equal(TableLeafUpdateStatus.NotFound, status);
    }

    [Fact]
    public void TryUpdate_returns_insufficient_space_and_preserves_original_row_when_page_is_full()
    {
        var page = TableLeafPage.Create(1);
        var smallPayload = new byte[1];
        var key = 1L;
        while (page.TryInsert(new TableLeafCell(key++, smallPayload)) == TableLeafInsertStatus.Success) { }

        // Pick the first existing cell and try to grow it beyond available space
        var firstCell = page.ReadCell(0);
        var hugePayload = new byte[page.FreeSpace + firstCell.GetRequiredSize() + 100];
        var status = page.TryUpdate(new TableLeafCell(firstCell.PrimaryKey, hugePayload));

        Assert.Equal(TableLeafUpdateStatus.InsufficientSpace, status);
        // Original data must still be intact
        Assert.True(page.TryGetCell(firstCell.PrimaryKey, out var unchanged));
        Assert.Equal(firstCell.Payload, unchanged.Payload);
    }

    // ── Existing tests ────────────────────────────────────────────────────────

    [Fact]
    public void TableLeafPage_keeps_cells_sorted_by_primary_key()
    {
        var page = TableLeafPage.Create(1);

        var firstInsert = page.TryInsert(new TableLeafCell(20, new byte[] { 0x20 }));
        var secondInsert = page.TryInsert(new TableLeafCell(5, new byte[] { 0x05 }));
        var thirdInsert = page.TryInsert(new TableLeafCell(10, new byte[] { 0x10 }));

        var cells = page.ReadAllCells();

        Assert.Equal(TableLeafInsertStatus.Success, firstInsert);
        Assert.Equal(TableLeafInsertStatus.Success, secondInsert);
        Assert.Equal(TableLeafInsertStatus.Success, thirdInsert);
        Assert.Collection(
            cells,
            cell => Assert.Equal(5, cell.PrimaryKey),
            cell => Assert.Equal(10, cell.PrimaryKey),
            cell => Assert.Equal(20, cell.PrimaryKey));
    }

    [Fact]
    public void TableLeafPage_rejects_duplicate_primary_keys()
    {
        var page = TableLeafPage.Create(1);

        var firstInsert = page.TryInsert(new TableLeafCell(7, new byte[] { 0x07 }));
        var duplicateInsert = page.TryInsert(new TableLeafCell(7, new byte[] { 0x99 }));

        Assert.Equal(TableLeafInsertStatus.Success, firstInsert);
        Assert.Equal(TableLeafInsertStatus.DuplicateKey, duplicateInsert);
        Assert.True(page.TryGetCell(7, out var cell));
        Assert.Equal(new byte[] { 0x07 }, cell.Payload);
    }

    [Fact]
    public void TableLeafPage_reports_page_full_when_there_is_not_enough_space()
    {
        var page = TableLeafPage.Create(1);
        var payload = new byte[250];
        TableLeafInsertStatus lastInsertResult = TableLeafInsertStatus.Success;
        var key = 1L;

        while (lastInsertResult == TableLeafInsertStatus.Success)
        {
            lastInsertResult = page.TryInsert(new TableLeafCell(key++, payload));
        }

        Assert.Equal(TableLeafInsertStatus.PageFull, lastInsertResult);
        Assert.True(page.FreeSpace < (BTreePageLayout.CellPointerSize + new TableLeafCell(key, payload).GetRequiredSize()));
    }
}
