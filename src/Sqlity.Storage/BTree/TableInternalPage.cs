using System.Buffers.Binary;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.BTree;

public sealed class TableInternalPage
{
    public TableInternalPage(PageBuffer page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var header = page.ReadHeader();
        if (header.PageType != PageType.TableInternal)
        {
            throw new InvalidOperationException($"Page {page.PageNumber} is {header.PageType}, not a table internal page.");
        }

        Page = page;
    }

    public PageBuffer Page { get; }

    public static TableInternalPage Create(uint pageNumber, uint leftmostChildPageId)
    {
        var buffer = PageBuffer.Create(pageNumber, PageType.TableInternal);
        var header = buffer.ReadHeader();
        buffer.WriteHeader(header with { SpecialPageId = leftmostChildPageId });
        return new TableInternalPage(buffer);
    }

    public ushort EntryCount => Page.ReadHeader().CellCount;

    // SpecialPageId stores the leftmost child pointer on internal pages.
    public uint LeftmostChildPageId => Page.ReadHeader().SpecialPageId;

    public int FreeSpace
    {
        get
        {
            var header = Page.ReadHeader();
            return header.CellContentStart - BTreePageLayout.GetMinimumFreeSpace(header.CellCount);
        }
    }

    public TableInternalInsertStatus TryInsert(TableInternalCell cell)
    {
        var header = Page.ReadHeader();
        var search = FindSlot(cell.DividerKey, header);
        if (search.Found)
        {
            return TableInternalInsertStatus.DuplicateKey;
        }

        if (FreeSpace < BTreePageLayout.CellPointerSize + BTreePageLayout.InternalCellSize)
        {
            return TableInternalInsertStatus.PageFull;
        }

        var newCellOffset = header.CellContentStart - BTreePageLayout.InternalCellSize;
        cell.WriteTo(Page.Span[newCellOffset..]);

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

        return TableInternalInsertStatus.Success;
    }

    /// <summary>
    /// Returns the child page ID that should be followed for the given search key.
    /// Keys strictly less than the first divider go to the leftmost child.
    /// </summary>
    public uint FindChildPageId(long searchKey)
    {
        var header = Page.ReadHeader();
        var low = 0;
        var high = header.CellCount - 1;
        var result = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var midKey = ReadCell((ushort)mid).DividerKey;

            if (searchKey >= midKey)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result < 0
            ? LeftmostChildPageId
            : ReadCell((ushort)result).RightChildPageId;
    }

    public TableInternalCell ReadCell(ushort index)
    {
        var header = Page.ReadHeader();
        if (index >= header.CellCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} is outside the page. Entry count is {header.CellCount}.");
        }

        var offset = ReadCellPointer(index);
        return TableInternalCell.ReadFrom(Page.ReadOnlySpan[offset..]);
    }

    public IReadOnlyList<TableInternalCell> ReadAllCells()
    {
        var header = Page.ReadHeader();
        var cells = new TableInternalCell[header.CellCount];

        for (ushort i = 0; i < header.CellCount; i++)
        {
            cells[i] = ReadCell(i);
        }

        return cells;
    }

    private SearchResult FindSlot(long key, PageHeader header)
    {
        var low = 0;
        var high = header.CellCount - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var midKey = ReadCell((ushort)mid).DividerKey;

            if (midKey == key)
            {
                return new SearchResult(true, (ushort)mid);
            }

            if (midKey < key)
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

    private ushort ReadCellPointer(ushort index)
    {
        var offset = BTreePageLayout.GetCellPointerOffset(index);
        return BinaryPrimitives.ReadUInt16LittleEndian(Page.ReadOnlySpan[offset..(offset + BTreePageLayout.CellPointerSize)]);
    }

    private void WriteCellPointer(ushort index, ushort cellOffset)
    {
        var offset = BTreePageLayout.GetCellPointerOffset(index);
        BinaryPrimitives.WriteUInt16LittleEndian(Page.Span[offset..(offset + BTreePageLayout.CellPointerSize)], cellOffset);
    }

    private readonly record struct SearchResult(bool Found, ushort InsertIndex);
}
