using Sqlity.Core;

namespace Sqlity.Storage.Pages;

/// <summary>
/// Static helpers for overflow pages used to store row payloads that exceed the usable
/// space of a single leaf page. Each overflow page stores a contiguous slice of the payload;
/// the chain of pages is linked via <see cref="PageHeader.SpecialPageId"/>.
/// </summary>
public static class OverflowPage
{
    /// <summary>Maximum payload bytes that fit in one overflow page.</summary>
    public static int DataCapacity => DbConstants.PageSize - PageHeader.Size;

    /// <summary>Returns the page ID of the next page in the chain (0 if this is the last).</summary>
    public static uint ReadNextPageId(PageBuffer page) => page.ReadHeader().SpecialPageId;

    /// <summary>Updates the next-page pointer in an overflow page.</summary>
    public static void SetNextPageId(PageBuffer page, uint nextPageId)
    {
        var header = page.ReadHeader();
        page.WriteHeader(header with { SpecialPageId = nextPageId });
    }

    /// <summary>Returns the writable payload region of an overflow page.</summary>
    public static Span<byte> DataSpan(PageBuffer page) => page.Span[PageHeader.Size..];

    /// <summary>Returns the read-only payload region of an overflow page.</summary>
    public static ReadOnlySpan<byte> DataReadOnlySpan(PageBuffer page) => page.ReadOnlySpan[PageHeader.Size..];
}
