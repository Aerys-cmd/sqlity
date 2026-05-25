using Sqlity.Core;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.Tests;

public sealed class PageHeaderTests
{
    [Fact]
    public void PageHeader_round_trips_through_binary_format()
    {
        var original = new PageHeader(
            PageType.TableLeaf,
            0b0000_0010,
            4,
            64,
            3072,
            11,
            12,
            2);

        Span<byte> buffer = stackalloc byte[PageHeader.Size];
        original.WriteTo(buffer);
        var roundTripped = PageHeader.ReadFrom(buffer);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void New_page_starts_with_cell_content_from_end_of_page()
    {
        var header = PageHeader.Create(3, PageType.TableLeaf);

        Assert.Equal((ushort)DbConstants.PageSize, header.CellContentStart);
        Assert.Equal((ushort)0, header.CellCount);
    }
}
