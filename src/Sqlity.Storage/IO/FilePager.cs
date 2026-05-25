using Sqlity.Core;
using Sqlity.Storage.Abstractions;
using Sqlity.Storage.Headers;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.IO;

public sealed class FilePager : IPager
{
    private readonly FileStream _stream;

    public FilePager(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _stream = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    public void InitializeNew()
    {
        if (_stream.Length != 0)
        {
            throw new InvalidOperationException("A new database can only be initialized on an empty file.");
        }

        var firstPage = new byte[DbConstants.PageSize];
        DatabaseHeader.CreateNew().WriteTo(firstPage);

        _stream.Position = 0;
        _stream.Write(firstPage, 0, firstPage.Length);
        _stream.Flush(flushToDisk: true);
    }

    public DatabaseHeader ReadDatabaseHeader()
    {
        EnsureInitialized();

        Span<byte> headerBuffer = stackalloc byte[DatabaseHeader.Size];
        _stream.Position = 0;
        _stream.ReadExactly(headerBuffer);
        return DatabaseHeader.ReadFrom(headerBuffer);
    }

    public void WriteDatabaseHeader(in DatabaseHeader header)
    {
        EnsureInitialized();

        Span<byte> headerBuffer = stackalloc byte[DatabaseHeader.Size];
        header.WriteTo(headerBuffer);

        _stream.Position = 0;
        _stream.Write(headerBuffer);
        _stream.Flush(flushToDisk: true);
    }

    public PageBuffer ReadPage(uint pageNumber)
    {
        if (pageNumber == DbConstants.HeaderPageNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Use ReadDatabaseHeader for page 0.");
        }

        var header = ReadDatabaseHeader();
        if (pageNumber >= header.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} is outside the file. Page count is {header.PageCount}.");
        }

        var bytes = new byte[DbConstants.PageSize];
        _stream.Position = GetPageOffset(pageNumber);
        _stream.ReadExactly(bytes, 0, bytes.Length);
        return new PageBuffer(pageNumber, bytes);
    }

    public void WritePage(PageBuffer page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.PageNumber == DbConstants.HeaderPageNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Use WriteDatabaseHeader for page 0.");
        }

        _stream.Position = GetPageOffset(page.PageNumber);
        _stream.Write(page.ReadOnlySpan);
        _stream.Flush(flushToDisk: true);
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
        if (pageNumber >= header.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} is outside the file. Page count is {header.PageCount}.");
        }

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

    public void Dispose() => _stream.Dispose();

    private void EnsureInitialized()
    {
        if (_stream.Length < DbConstants.PageSize)
        {
            throw new InvalidOperationException("The file is not initialized as a Sqlity database yet.");
        }
    }

    private static long GetPageOffset(uint pageNumber) => checked((long)pageNumber * DbConstants.PageSize);
}
