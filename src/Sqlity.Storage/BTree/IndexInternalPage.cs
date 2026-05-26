using System.Buffers.Binary;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.BTree;

/// <summary>
/// Slotted page that stores <see cref="IndexInternalCell"/> entries with variable-length
/// byte-array divider keys. Key comparisons use lexicographic byte order to match
/// <see cref="IndexKeyEncoder"/> encoding.
/// </summary>
internal sealed class IndexInternalPage
{
    public IndexInternalPage(PageBuffer page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var header = page.ReadHeader();
        if (header.PageType != PageType.IndexInternal)
            throw new InvalidOperationException($"Page {page.PageNumber} is {header.PageType}, not an index internal page.");

        Page = page;
    }

    public PageBuffer Page { get; }

    public static IndexInternalPage Create(uint pageNumber, uint leftmostChildPageId)
    {
        var buffer = PageBuffer.Create(pageNumber, PageType.IndexInternal);
        var header = buffer.ReadHeader();
        buffer.WriteHeader(header with { SpecialPageId = leftmostChildPageId });
        return new IndexInternalPage(buffer);
    }

    public ushort EntryCount => Page.ReadHeader().CellCount;

    public uint LeftmostChildPageId => Page.ReadHeader().SpecialPageId;

    public int FreeSpace
    {
        get
        {
            var header = Page.ReadHeader();
            return header.CellContentStart - BTreePageLayout.GetMinimumFreeSpace(header.CellCount);
        }
    }

    public IndexInternalInsertStatus TryInsert(in IndexInternalCell cell)
    {
        var header = Page.ReadHeader();
        var search = FindSlot(cell.DividerKey, header);

        if (search.Found)
            return IndexInternalInsertStatus.DuplicateKey;

        var requiredSpace = BTreePageLayout.CellPointerSize + cell.GetRequiredSize();
        if (FreeSpace < requiredSpace)
            return IndexInternalInsertStatus.PageFull;

        var newCellOffset = header.CellContentStart - cell.GetRequiredSize();
        cell.WriteTo(Page.Span[newCellOffset..]);

        var pointerArrayStart = BTreePageLayout.GetCellPointerOffset(search.InsertIndex);
        var bytesToShift = (header.CellCount - search.InsertIndex) * BTreePageLayout.CellPointerSize;
        if (bytesToShift > 0)
        {
            Page.Span
                .Slice(pointerArrayStart, bytesToShift)
                .CopyTo(Page.Span[(pointerArrayStart + BTreePageLayout.CellPointerSize)..]);
        }

        WriteCellPointer(search.InsertIndex, checked((ushort)newCellOffset));

        Page.WriteHeader(header with
        {
            CellCount = checked((ushort)(header.CellCount + 1)),
            CellContentStart = checked((ushort)newCellOffset)
        });

        return IndexInternalInsertStatus.Success;
    }

    /// <summary>
    /// Returns the child page ID to follow for the given search key.
    /// Keys strictly less than the first divider go to the leftmost child.
    /// </summary>
    public uint FindChildPageId(ReadOnlySpan<byte> searchKey)
    {
        var header = Page.ReadHeader();
        var low = 0;
        var high = header.CellCount - 1;
        var result = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var midKey = ReadCellKey((ushort)mid);

            if (searchKey.SequenceCompareTo(midKey) >= 0)
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

    public IndexInternalCell ReadCell(ushort index)
    {
        var header = Page.ReadHeader();
        if (index >= header.CellCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        var offset = ReadCellPointer(index);
        return IndexInternalCell.ReadFrom(Page.ReadOnlySpan[offset..]);
    }

    public IReadOnlyList<IndexInternalCell> ReadAllCells()
    {
        var header = Page.ReadHeader();
        var cells = new IndexInternalCell[header.CellCount];

        for (ushort i = 0; i < header.CellCount; i++)
            cells[i] = ReadCell(i);

        return cells;
    }

    /// <summary>Mirrors <see cref="TableInternalPage.TryRemoveChildReference"/>.</summary>
    public RemoveChildStatus TryRemoveChildReference(uint childPageId)
    {
        var header = Page.ReadHeader();

        if (LeftmostChildPageId == childPageId)
        {
            if (header.CellCount == 0)
                return RemoveChildStatus.LastChildRemoved;

            var firstCell = ReadCell(0);
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
        var cell = IndexInternalCell.ReadFrom(Page.ReadOnlySpan[cellOffset..]);
        var cellSize = cell.GetRequiredSize();

        // Shift cells physically above (lower offset) up to fill the gap.
        var bytesToCompact = cellOffset - header.CellContentStart;
        if (bytesToCompact > 0)
        {
            Page.Span
                .Slice(header.CellContentStart, bytesToCompact)
                .CopyTo(Page.Span[(header.CellContentStart + cellSize)..]);
        }

        for (ushort i = 0; i < header.CellCount; i++)
        {
            if (i == index) continue;

            var pointer = ReadCellPointer(i);
            if (pointer < cellOffset)
                WriteCellPointer(i, checked((ushort)(pointer + cellSize)));
        }

        var pointerOffset = BTreePageLayout.GetCellPointerOffset(index);
        var trailingBytes = (header.CellCount - index - 1) * BTreePageLayout.CellPointerSize;
        if (trailingBytes > 0)
        {
            Page.Span
                .Slice(pointerOffset + BTreePageLayout.CellPointerSize, trailingBytes)
                .CopyTo(Page.Span[pointerOffset..]);
        }

        Page.WriteHeader(header with
        {
            CellCount = checked((ushort)(header.CellCount - 1)),
            CellContentStart = checked((ushort)(header.CellContentStart + cellSize))
        });
    }

    public ReadOnlySpan<byte> ReadCellKey(ushort index)
    {
        var offset = ReadCellPointer(index);
        var span = Page.ReadOnlySpan[offset..];
        var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(span[..sizeof(ushort)]);
        return span[sizeof(ushort)..(sizeof(ushort) + keyLen)];
    }

    private SearchResult FindSlot(ReadOnlySpan<byte> key, PageHeader header)
    {
        var low = 0;
        var high = header.CellCount - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var midKey = ReadCellKey((ushort)mid);
            var cmp = key.SequenceCompareTo(midKey);

            if (cmp == 0) return new SearchResult(true, (ushort)mid);
            if (cmp > 0) low = mid + 1;
            else high = mid - 1;
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
