using Sqlity.Storage.IO;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.Tests;

public sealed class BufferedPagerTests
{
    [Fact]
    public void ReadPage_returns_correct_data_on_cache_miss_and_hit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var inner = new FilePager(path);
            inner.InitializeNew();
            var pageNumber = inner.AllocatePage(PageType.TableLeaf);

            using var buffered = new BufferedPager(inner, capacity: 8);

            // First read — cache miss, goes to file.
            var page1 = buffered.ReadPage(pageNumber);
            Assert.Equal(pageNumber, page1.PageNumber);

            // Second read — should be served from cache (same data).
            var page2 = buffered.ReadPage(pageNumber);
            Assert.Equal(pageNumber, page2.PageNumber);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WritePage_marks_entry_dirty_and_flushes_on_commit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            // Setup: allocate a page on a plain file pager, then close it.
            uint pageNumber;
            {
                using var setup = new FilePager(path);
                setup.InitializeNew();
                setup.BeginTransaction();
                pageNumber = setup.AllocatePage(PageType.TableLeaf);
                setup.Commit();
            }

            // Open via BufferedPager and write a sentinel byte.
            {
                using var inner = new FilePager(path);
                inner.RecoverIfNeeded();
                using var buffered = new BufferedPager(inner, capacity: 8);

                var page = buffered.ReadPage(pageNumber);
                page.Span[PageHeader.Size] = 0xAB;

                buffered.BeginTransaction();
                buffered.WritePage(page);
                buffered.Commit();
            }

            // Re-open with a plain FilePager to verify the byte was persisted.
            using var verify = new FilePager(path);
            var verifiedPage = verify.ReadPage(pageNumber);
            Assert.Equal(0xAB, verifiedPage.ReadOnlySpan[PageHeader.Size]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Rollback_discards_dirty_pages_and_restores_inner_pager_state()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var inner = new FilePager(path);
            inner.InitializeNew();
            inner.BeginTransaction();
            var pageNumber = inner.AllocatePage(PageType.TableLeaf);
            inner.Commit();

            var originalByte = inner.ReadPage(pageNumber).ReadOnlySpan[PageHeader.Size];

            using var buffered = new BufferedPager(inner, capacity: 8);

            buffered.BeginTransaction();
            var page = buffered.ReadPage(pageNumber);
            page.Span[PageHeader.Size] = (byte)(originalByte ^ 0xFF); // flip bits
            buffered.WritePage(page);
            buffered.Rollback();

            // After rollback the file should have the original byte.
            var restored = inner.ReadPage(pageNumber);
            Assert.Equal(originalByte, restored.ReadOnlySpan[PageHeader.Size]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LRU_eviction_flushes_dirty_page_when_cache_is_full()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            const int capacity = 4;

            using var inner = new FilePager(path);
            inner.InitializeNew();

            // Allocate capacity+1 pages so we can force eviction.
            var pages = new uint[capacity + 1];
            inner.BeginTransaction();
            for (int i = 0; i < pages.Length; i++)
                pages[i] = inner.AllocatePage(PageType.TableLeaf);
            inner.Commit();

            using var buffered = new BufferedPager(inner, capacity: capacity);

            // Write a sentinel byte into the first page via the buffered pager (no transaction).
            var firstPage = buffered.ReadPage(pages[0]);
            firstPage.Span[PageHeader.Size] = 0xCC;
            buffered.WritePage(firstPage);

            // Fill the cache up to capacity with the remaining pages, forcing eviction of firstPage.
            for (int i = 1; i <= capacity; i++)
                _ = buffered.ReadPage(pages[i]);

            // The evicted dirty page should have been flushed to the inner pager.
            var flushed = inner.ReadPage(pages[0]);
            Assert.Equal(0xCC, flushed.ReadOnlySpan[PageHeader.Size]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InMemoryPager_wrapped_in_BufferedPager_works_end_to_end()
    {
        var memPager = new InMemoryPager();
        memPager.InitializeNew();

        using var buffered = new BufferedPager(memPager, capacity: 4);

        var pageNumber = buffered.AllocatePage(PageType.TableLeaf);
        var page = buffered.ReadPage(pageNumber);
        page.Span[PageHeader.Size] = 0x99;
        buffered.WritePage(page);

        var readBack = buffered.ReadPage(pageNumber);
        Assert.Equal(0x99, readBack.ReadOnlySpan[PageHeader.Size]);
    }
}
