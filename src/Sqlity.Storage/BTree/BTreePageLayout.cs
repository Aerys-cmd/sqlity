using Sqlity.Storage.Pages;

namespace Sqlity.Storage.BTree;

public static class BTreePageLayout
{
    public const int CellPointerSize = sizeof(ushort);
    public const int LeafCellHeaderSize = sizeof(long) + sizeof(ushort);
    public const int InternalCellSize = sizeof(long) + sizeof(uint);

    public static int GetPointerArrayOffset() => PageHeader.Size;

    public static int GetCellPointerOffset(ushort slotIndex) =>
        GetPointerArrayOffset() + (slotIndex * CellPointerSize);

    public static int GetMinimumFreeSpace(ushort cellCount) =>
        PageHeader.Size + (cellCount * CellPointerSize);

    public static int GetAvailableSpace(PageHeader header) =>
        header.CellContentStart - GetMinimumFreeSpace(header.CellCount);
}
