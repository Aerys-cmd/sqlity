using System.Buffers.Binary;

namespace Sqlity.Storage.Pages;

public static class FreeListPage
{
    private const int NextFreePageIdOffset = PageHeader.Size;

    public static void Initialize(PageBuffer page, uint nextFreePageId)
    {
        ArgumentNullException.ThrowIfNull(page);

        page.WriteHeader(PageHeader.Create(page.PageNumber, PageType.FreeList));
        SetNextFreePageId(page, nextFreePageId);
    }

    public static uint ReadNextFreePageId(PageBuffer page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var header = page.ReadHeader();
        if (header.PageType != PageType.FreeList)
        {
            throw new InvalidOperationException($"Page {page.PageNumber} is {header.PageType}, not a free-list page.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(page.ReadOnlySpan[NextFreePageIdOffset..(NextFreePageIdOffset + sizeof(uint))]);
    }

    public static void SetNextFreePageId(PageBuffer page, uint nextFreePageId)
    {
        ArgumentNullException.ThrowIfNull(page);

        BinaryPrimitives.WriteUInt32LittleEndian(page.Span[NextFreePageIdOffset..(NextFreePageIdOffset + sizeof(uint))], nextFreePageId);
    }
}
