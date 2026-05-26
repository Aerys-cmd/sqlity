using System.Buffers.Binary;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.BTree;

/// <summary>
/// Slotted page that stores <see cref="IndexLeafCell"/> entries sorted by variable-length
/// byte-array keys. The comparison uses <see cref="MemoryExtensions.SequenceCompareTo"/>
/// (i.e. lexicographic / ordinal), which matches the encoding produced by
/// <see cref="IndexKeyEncoder"/>.
/// </summary>
internal sealed class IndexLeafPage
{
    public IndexLeafPage(PageBuffer page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var header = page.ReadHeader();
        if (header.PageType != PageType.IndexLeaf)
            throw new InvalidOperationException($"Page {page.PageNumber} is {header.PageType}, not an index leaf page.");

        Page = page;
    }

    public PageBuffer Page { get; }

    public static IndexLeafPage Create(uint pageNumber) =>
        new(PageBuffer.Create(pageNumber, PageType.IndexLeaf));

    public ushort CellCount => Page.ReadHeader().CellCount;

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

    public IndexLeafInsertStatus TryInsert(in IndexLeafCell cell)
    {
        var header = Page.ReadHeader();
        var search = FindSlot(cell.Key, header);

        if (search.Found)
            return IndexLeafInsertStatus.DuplicateKey;

        var requiredSpace = BTreePageLayout.CellPointerSize + cell.GetRequiredSize();
        if (FreeSpace < requiredSpace)
            return IndexLeafInsertStatus.PageFull;

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

        return IndexLeafInsertStatus.Success;
    }

    public IndexLeafDeleteStatus TryDelete(ReadOnlySpan<byte> key)
    {
        var header = Page.ReadHeader();
        var search = FindSlot(key, header);

        if (!search.Found)
            return IndexLeafDeleteStatus.NotFound;

        var cellOffset = ReadCellPointer(search.InsertIndex);
        var cell = IndexLeafCell.ReadFrom(Page.ReadOnlySpan[cellOffset..]);
        var cellSize = cell.GetRequiredSize();

        var bytesToCompact = cellOffset - header.CellContentStart;
        if (bytesToCompact > 0)
        {
            Page.Span
                .Slice(header.CellContentStart, bytesToCompact)
                .CopyTo(Page.Span[(header.CellContentStart + cellSize)..]);
        }

        for (ushort i = 0; i < header.CellCount; i++)
        {
            if (i == search.InsertIndex) continue;

            var pointer = ReadCellPointer(i);
            if (pointer < cellOffset)
                WriteCellPointer(i, checked((ushort)(pointer + cellSize)));
        }

        var pointerOffset = BTreePageLayout.GetCellPointerOffset(search.InsertIndex);
        var trailingPointerBytes = (header.CellCount - search.InsertIndex - 1) * BTreePageLayout.CellPointerSize;
        if (trailingPointerBytes > 0)
        {
            Page.Span
                .Slice(pointerOffset + BTreePageLayout.CellPointerSize, trailingPointerBytes)
                .CopyTo(Page.Span[pointerOffset..]);
        }

        Page.WriteHeader(header with
        {
            CellCount = checked((ushort)(header.CellCount - 1)),
            CellContentStart = checked((ushort)(header.CellContentStart + cellSize))
        });

        return IndexLeafDeleteStatus.Success;
    }

    public bool TryGetCell(ReadOnlySpan<byte> key, out IndexLeafCell cell)
    {
        var header = Page.ReadHeader();
        var search = FindSlot(key, header);

        if (!search.Found)
        {
            cell = default;
            return false;
        }

        cell = ReadCell(search.InsertIndex);
        return true;
    }

    public IReadOnlyList<IndexLeafCell> ReadAllCells()
    {
        var header = Page.ReadHeader();
        var cells = new IndexLeafCell[header.CellCount];

        for (ushort i = 0; i < header.CellCount; i++)
            cells[i] = ReadCell(i);

        return cells;
    }

    public IndexLeafCell ReadCell(ushort slotIndex)
    {
        var header = Page.ReadHeader();
        if (slotIndex >= header.CellCount)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));

        var offset = ReadCellPointer(slotIndex);
        return IndexLeafCell.ReadFrom(Page.ReadOnlySpan[offset..]);
    }

    /// <summary>
    /// Reads only the key portion of the cell at the given slot without deserialising the PK.
    /// Used in binary search to avoid full cell allocation when only the key is needed.
    /// </summary>
    public ReadOnlySpan<byte> ReadCellKey(ushort slotIndex)
    {
        var offset = ReadCellPointer(slotIndex);
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
