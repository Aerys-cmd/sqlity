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
