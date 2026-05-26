using Sqlity.Core;
using Sqlity.Storage.Abstractions;
using Sqlity.Storage.Headers;
using Sqlity.Storage.Pages;

namespace Sqlity.Benchmarks;

/// <summary>
/// An in-memory IPager implementation that stores all pages in a dictionary.
/// Used by benchmarks to isolate CPU and logic costs from disk I/O.
/// </summary>
internal sealed class InMemoryPager : IPager
{
    private DatabaseHeader _header;
    private readonly Dictionary<uint, byte[]> _pages = new();
    private bool _initialized;

    public void InitializeNew()
    {
        _header = DatabaseHeader.CreateNew();
        _initialized = true;
    }

    public DatabaseHeader ReadDatabaseHeader()
    {
        EnsureInitialized();
        return _header;
    }

    public void WriteDatabaseHeader(in DatabaseHeader header)
    {
        EnsureInitialized();
        _header = header;
    }

    public PageBuffer ReadPage(uint pageNumber)
    {
        if (pageNumber == DbConstants.HeaderPageNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Use ReadDatabaseHeader for page 0.");
        }

        if (!_pages.TryGetValue(pageNumber, out var stored))
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} has not been allocated.");
        }

        // Return a copy so callers cannot alias the internal buffer.
        var copy = new byte[DbConstants.PageSize];
        stored.CopyTo(copy, 0);
        return new PageBuffer(pageNumber, copy);
    }

    public void WritePage(PageBuffer page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.PageNumber == DbConstants.HeaderPageNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Use WriteDatabaseHeader for page 0.");
        }

        var bytes = new byte[DbConstants.PageSize];
        page.ReadOnlySpan.CopyTo(bytes);
        _pages[page.PageNumber] = bytes;
    }

    public uint AllocatePage(PageType pageType)
    {
        var header = ReadDatabaseHeader();

        if (header.FreeListHeadPageId != 0)
        {
            var recycledPageNumber = header.FreeListHeadPageId;
            var recycledPage = ReadPage(recycledPageNumber);
            var nextFreePageId = FreeListPage.ReadNextFreePageId(recycledPage);

            var page = PageBuffer.Create(recycledPageNumber, pageType);
            WritePage(page);

            WriteDatabaseHeader(
                header with
                {
                    FreeListHeadPageId = nextFreePageId,
                    FreePageCount = header.FreePageCount - 1
                });

            return recycledPageNumber;
        }

        var newPageNumber = header.PageCount;
        var newPage = PageBuffer.Create(newPageNumber, pageType);
        WritePage(newPage);

        WriteDatabaseHeader(header with { PageCount = newPageNumber + 1 });
        return newPageNumber;
    }

    public void ReleasePage(uint pageNumber)
    {
        if (pageNumber == DbConstants.HeaderPageNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "The database header page cannot be released.");
        }

        var header = ReadDatabaseHeader();

        var page = new PageBuffer(pageNumber);
        FreeListPage.Initialize(page, header.FreeListHeadPageId);
        WritePage(page);

        WriteDatabaseHeader(
            header with
            {
                FreeListHeadPageId = pageNumber,
                FreePageCount = header.FreePageCount + 1
            });
    }

    public void Dispose() { }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("InMemoryPager has not been initialized. Call InitializeNew() first.");
        }
    }
}
