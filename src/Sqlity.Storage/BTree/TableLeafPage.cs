using System.Buffers.Binary;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.BTree;

public sealed class TableLeafPage
{
    public TableLeafPage(PageBuffer page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var header = page.ReadHeader();
        if (header.PageType != PageType.TableLeaf)
        {
            throw new InvalidOperationException($"Page {page.PageNumber} is {header.PageType}, not a table leaf page.");
        }

        Page = page;
    }

    public PageBuffer Page { get; }

    public static TableLeafPage Create(uint pageNumber) => new(PageBuffer.Create(pageNumber, PageType.TableLeaf));

    public ushort CellCount => Page.ReadHeader().CellCount;

    // SpecialPageId stores the next-leaf pointer on leaf pages (0 = no next leaf).
    public uint NextLeafPageId => Page.ReadHeader().SpecialPageId;

    public void SetNextLeafPageId(uint nextPageId)
    {
        var header = Page.ReadHeader();
        Page.WriteHeader(header with { SpecialPageId = nextPageId });
    }

    public int FreeSpace
    {
        get
        {
            var header = Page.ReadHeader();
            return header.CellContentStart - BTreePageLayout.GetMinimumFreeSpace(header.CellCount);
        }
    }

    public TableLeafInsertStatus TryInsert(in TableLeafCell cell)
    {
        var header = Page.ReadHeader();
        var search = FindSlot(cell.PrimaryKey, header);
        if (search.Found)
        {
            return TableLeafInsertStatus.DuplicateKey;
        }

        var requiredSpace = BTreePageLayout.CellPointerSize + cell.GetRequiredSize();
        if (FreeSpace < requiredSpace)
        {
            return TableLeafInsertStatus.PageFull;
        }

        var newCellOffset = header.CellContentStart - cell.GetRequiredSize();
        cell.WriteTo(Page.Span[newCellOffset..]);

        // Slot array compaction is the core slotted-page trick: cells can move in the payload
        // region while logical order is preserved by the ordered pointer array.
        var pointerArrayStart = BTreePageLayout.GetCellPointerOffset(search.InsertIndex);
        var bytesToShift = (header.CellCount - search.InsertIndex) * BTreePageLayout.CellPointerSize;
        if (bytesToShift > 0)
        {
            Page.Span.Slice(pointerArrayStart, bytesToShift).CopyTo(Page.Span[(pointerArrayStart + BTreePageLayout.CellPointerSize)..]);
        }

        WriteCellPointer(search.InsertIndex, checked((ushort)newCellOffset));

        Page.WriteHeader(
            header with
            {
                CellCount = checked((ushort)(header.CellCount + 1)),
                CellContentStart = checked((ushort)newCellOffset)
            });

        return TableLeafInsertStatus.Success;
    }

    public TableLeafDeleteStatus TryDelete(long primaryKey)
    {
        var header = Page.ReadHeader();
        var search = FindSlot(primaryKey, header);
        if (!search.Found)
        {
            return TableLeafDeleteStatus.NotFound;
        }

        var cellOffset = ReadCellPointer(search.InsertIndex);
        var cell = TableLeafCell.ReadFrom(Page.ReadOnlySpan[cellOffset..]);
        var cellSize = cell.GetRequiredSize();

        // Cells at offsets [CellContentStart, cellOffset) were inserted more recently and sit
        // physically above the deleted cell. Shift them toward higher offsets by cellSize to
        // close the gap left by the removed cell.
        var bytesToCompact = cellOffset - header.CellContentStart;
        if (bytesToCompact > 0)
        {
            Page.Span
                .Slice(header.CellContentStart, bytesToCompact)
                .CopyTo(Page.Span[(header.CellContentStart + cellSize)..]);
        }

        // Update pointers for the cells that just moved (those at offsets below cellOffset).
        for (ushort i = 0; i < header.CellCount; i++)
        {
            if (i == search.InsertIndex)
            {
                continue;
            }

            var pointer = ReadCellPointer(i);
            if (pointer < cellOffset)
            {
                WriteCellPointer(i, checked((ushort)(pointer + cellSize)));
            }
        }

        // Remove the deleted cell's slot from the pointer array by shifting
        // all subsequent slots one position to the left.
        var pointerOffset = BTreePageLayout.GetCellPointerOffset(search.InsertIndex);
        var trailingPointerBytes = (header.CellCount - search.InsertIndex - 1) * BTreePageLayout.CellPointerSize;
        if (trailingPointerBytes > 0)
        {
            Page.Span
                .Slice(pointerOffset + BTreePageLayout.CellPointerSize, trailingPointerBytes)
                .CopyTo(Page.Span[pointerOffset..]);
        }

        Page.WriteHeader(
            header with
            {
                CellCount = checked((ushort)(header.CellCount - 1)),
                CellContentStart = checked((ushort)(header.CellContentStart + cellSize))
            });

        return TableLeafDeleteStatus.Success;
    }

    public TableLeafUpdateStatus TryUpdate(in TableLeafCell cell)
    {
        var header = Page.ReadHeader();
        var search = FindSlot(cell.PrimaryKey, header);
        if (!search.Found)
        {
            return TableLeafUpdateStatus.NotFound;
        }

        var existingCellOffset = ReadCellPointer(search.InsertIndex);
        var existingCell = TableLeafCell.ReadFrom(Page.ReadOnlySpan[existingCellOffset..]);
        var oldSize = existingCell.GetRequiredSize();
        var newSize = cell.GetRequiredSize();

        // Same size: overwrite in place — no compaction or pointer changes needed.
        if (newSize == oldSize)
        {
            cell.WriteTo(Page.Span[existingCellOffset..]);
            return TableLeafUpdateStatus.Success;
        }

        // Growing update: check space before touching anything to avoid data loss.
        if (newSize > oldSize && newSize - oldSize > FreeSpace)
        {
            return TableLeafUpdateStatus.InsufficientSpace;
        }

        // Size changed: delete the old cell and re-insert the new one.
        // TryDelete compacts the page; TryInsert places the cell at the new CellContentStart.
        // Safe because we already verified sufficient space for growing updates above.
        var deleteStatus = TryDelete(cell.PrimaryKey);
        if (deleteStatus != TableLeafDeleteStatus.Success)
        {
            return TableLeafUpdateStatus.NotFound;
        }

        return TryInsert(cell) switch
        {
            TableLeafInsertStatus.Success => TableLeafUpdateStatus.Success,
            TableLeafInsertStatus.PageFull => TableLeafUpdateStatus.InsufficientSpace,
            _ => TableLeafUpdateStatus.NotFound
        };
    }

    public bool TryGetCell(long primaryKey, out TableLeafCell cell)
    {
        var header = Page.ReadHeader();
        var search = FindSlot(primaryKey, header);
        if (!search.Found)
        {
            cell = default;
            return false;
        }

        cell = ReadCell(search.InsertIndex);
        return true;
    }

    public IReadOnlyList<TableLeafCell> ReadAllCells()
    {
        var header = Page.ReadHeader();
        var cells = new TableLeafCell[header.CellCount];

        for (ushort index = 0; index < header.CellCount; index++)
        {
            cells[index] = ReadCell(index);
        }

        return cells;
    }

    public TableLeafCell ReadCell(ushort slotIndex)
    {
        var header = Page.ReadHeader();
        if (slotIndex >= header.CellCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex), $"Slot {slotIndex} is outside the page. Cell count is {header.CellCount}.");
        }

        var cellOffset = ReadCellPointer(slotIndex);
        return TableLeafCell.ReadFrom(Page.ReadOnlySpan[cellOffset..]);
    }

    public long ReadPrimaryKey(ushort slotIndex)
    {
        var header = Page.ReadHeader();
        if (slotIndex >= header.CellCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex), $"Slot {slotIndex} is outside the page. Cell count is {header.CellCount}.");
        }

        var cellOffset = ReadCellPointer(slotIndex);
        return BinaryPrimitives.ReadInt64LittleEndian(Page.ReadOnlySpan[cellOffset..(cellOffset + sizeof(long))]);
    }

    private SearchResult FindSlot(long primaryKey, PageHeader header)
    {
        var low = 0;
        var high = header.CellCount - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var midKey = ReadPrimaryKey((ushort)mid);

            if (midKey == primaryKey)
            {
                return new SearchResult(true, (ushort)mid);
            }

            if (midKey < primaryKey)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return new SearchResult(false, checked((ushort)low));
    }

    private ushort ReadCellPointer(ushort slotIndex)
    {
        var offset = BTreePageLayout.GetCellPointerOffset(slotIndex);
        return BinaryPrimitives.ReadUInt16LittleEndian(Page.ReadOnlySpan[offset..(offset + BTreePageLayout.CellPointerSize)]);
    }

    private void WriteCellPointer(ushort slotIndex, ushort cellOffset)
    {
        var offset = BTreePageLayout.GetCellPointerOffset(slotIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(Page.Span[offset..(offset + BTreePageLayout.CellPointerSize)], cellOffset);
    }

    private readonly record struct SearchResult(bool Found, ushort InsertIndex);
}
