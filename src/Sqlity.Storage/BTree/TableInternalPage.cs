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

    /// <summary>
    /// Removes the slot that points to <paramref name="childPageId"/> as a child page.
    /// <list type="bullet">
    ///   <item>If the child is <see cref="LeftmostChildPageId"/>: promotes the first cell's
    ///   right-child pointer as the new leftmost child and removes that cell.</item>
    ///   <item>Otherwise: finds the cell whose <c>RightChildPageId</c> equals the argument
    ///   and removes it.</item>
    /// </list>
    /// </summary>
    public RemoveChildStatus TryRemoveChildReference(uint childPageId)
    {
        var header = Page.ReadHeader();

        if (LeftmostChildPageId == childPageId)
        {
            if (header.CellCount == 0)
            {
                // Only child — the tree is now structurally empty at this level.
                return RemoveChildStatus.LastChildRemoved;
            }

            var firstCell = ReadCell(0);
            // Update the leftmost pointer to the promoted right child and pass the
            // updated header into RemoveCellAtIndex so it doesn't overwrite SpecialPageId.
            var updatedHeader = header with { SpecialPageId = firstCell.RightChildPageId };
            Page.WriteHeader(updatedHeader);
            RemoveCellAtIndex(0, updatedHeader);
            return RemoveChildStatus.Success;
        }

        for (ushort i = 0; i < header.CellCount; i++)
        {
            if (ReadCell(i).RightChildPageId == childPageId)
            {
                RemoveCellAtIndex(i, header);
                return RemoveChildStatus.Success;
            }
        }

        return RemoveChildStatus.NotFound;
    }

    private void RemoveCellAtIndex(ushort index, PageHeader header)
    {
        var cellOffset = ReadCellPointer(index);

        // Internal cells are fixed size. Shift cells physically above (lower offset) up by
        // InternalCellSize to fill the hole left by the removed cell.
        var bytesToCompact = cellOffset - header.CellContentStart;
        if (bytesToCompact > 0)
        {
            Page.Span
                .Slice(header.CellContentStart, bytesToCompact)
                .CopyTo(Page.Span[(header.CellContentStart + BTreePageLayout.InternalCellSize)..]);
        }

        // Adjust pointers for cells that physically moved.
        for (ushort i = 0; i < header.CellCount; i++)
        {
            if (i == index) continue;

            var pointer = ReadCellPointer(i);
            if (pointer < cellOffset)
            {
                WriteCellPointer(i, checked((ushort)(pointer + BTreePageLayout.InternalCellSize)));
            }
        }

        // Remove the slot pointer by shifting all subsequent slots one position to the left.
        var pointerOffset = BTreePageLayout.GetCellPointerOffset(index);
        var trailingBytes = (header.CellCount - index - 1) * BTreePageLayout.CellPointerSize;
        if (trailingBytes > 0)
        {
            Page.Span
                .Slice(pointerOffset + BTreePageLayout.CellPointerSize, trailingBytes)
                .CopyTo(Page.Span[pointerOffset..]);
        }

        Page.WriteHeader(
            header with
            {
                CellCount = checked((ushort)(header.CellCount - 1)),
                CellContentStart = checked((ushort)(header.CellContentStart + BTreePageLayout.InternalCellSize))
            });
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

public enum RemoveChildStatus
{
    Success,
    NotFound,
    /// <summary>
    /// Returned when the last remaining child pointer was removed. The caller should
    /// collapse the internal page (e.g. reformat the root as an empty leaf).
    /// </summary>
    LastChildRemoved
}
