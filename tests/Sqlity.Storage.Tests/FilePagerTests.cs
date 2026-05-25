using Sqlity.Storage.IO;
using Sqlity.Storage.BTree;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.Tests;

public sealed class FilePagerTests
{
    [Fact]
    public void FilePager_initializes_a_single_header_page_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var pager = new FilePager(path);
            pager.InitializeNew();

            var header = pager.ReadDatabaseHeader();

            Assert.Equal<uint>(1, header.PageCount);
            Assert.Equal<uint>(0, header.FreeListHeadPageId);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void FilePager_reuses_pages_from_the_free_list()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var pager = new FilePager(path);
            pager.InitializeNew();

            var firstPage = pager.AllocatePage(PageType.TableLeaf);
            var secondPage = pager.AllocatePage(PageType.TableLeaf);
            pager.ReleasePage(firstPage);

            var recycledPage = pager.AllocatePage(PageType.TableInternal);
            var header = pager.ReadDatabaseHeader();

            Assert.Equal<uint>(1, firstPage);
            Assert.Equal<uint>(2, secondPage);
            Assert.Equal(firstPage, recycledPage);
            Assert.Equal<uint>(0, header.FreeListHeadPageId);
            Assert.Equal<uint>(0, header.FreePageCount);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void FilePager_persists_table_leaf_page_contents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");

        try
        {
            using var pager = new FilePager(path);
            pager.InitializeNew();

            var pageNumber = pager.AllocatePage(PageType.TableLeaf);
            var page = new TableLeafPage(pager.ReadPage(pageNumber));
            page.TryInsert(new TableLeafCell(3, new byte[] { 0x30, 0x31 }));
            page.TryInsert(new TableLeafCell(1, new byte[] { 0x10 }));
            page.TryInsert(new TableLeafCell(2, new byte[] { 0x20 }));

            pager.WritePage(page.Page);

            var reloaded = new TableLeafPage(pager.ReadPage(pageNumber));
            var cells = reloaded.ReadAllCells();

            Assert.Collection(
                cells,
                cell =>
                {
                    Assert.Equal(1, cell.PrimaryKey);
                    Assert.Equal(new byte[] { 0x10 }, cell.Payload);
                },
                cell =>
                {
                    Assert.Equal(2, cell.PrimaryKey);
                    Assert.Equal(new byte[] { 0x20 }, cell.Payload);
                },
                cell =>
                {
                    Assert.Equal(3, cell.PrimaryKey);
                    Assert.Equal(new byte[] { 0x30, 0x31 }, cell.Payload);
                });
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
