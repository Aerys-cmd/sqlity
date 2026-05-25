using Sqlity.Storage.BTree;

namespace Sqlity.Storage.Tests;

public sealed class TableLeafPageTests
{
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
