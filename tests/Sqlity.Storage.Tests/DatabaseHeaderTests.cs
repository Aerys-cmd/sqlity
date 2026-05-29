using Sqlity.Core;
using Sqlity.Storage.Headers;

namespace Sqlity.Storage.Tests;

public sealed class DatabaseHeaderTests
{
    [Fact]
    public void DatabaseHeader_round_trips_through_binary_format()
    {
        var original = new DatabaseHeader(
            DbConstants.FormatVersion,
            DbConstants.PageSize,
            42,
            7,
            19,
            3,
            5,
            IndexCatalogRootPageId: 11,
            ViewCatalogRootPageId: 0,
            StatsCatalogRootPageId: 0);

        Span<byte> buffer = stackalloc byte[DatabaseHeader.Size];
        original.WriteTo(buffer);
        var roundTripped = DatabaseHeader.ReadFrom(buffer);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void CreateNew_uses_single_header_page_and_no_free_pages()
    {
        var header = DatabaseHeader.CreateNew();

        Assert.Equal<uint>(1, header.PageCount);
        Assert.Equal<uint>(0, header.RootPageId);
        Assert.Equal<uint>(0, header.FreeListHeadPageId);
        Assert.Equal<uint>(0, header.FreePageCount);
    }
}
