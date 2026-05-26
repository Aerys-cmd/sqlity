using Sqlity.Core;
using Sqlity.Storage.Abstractions;
using Sqlity.Storage.Headers;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.IO;

public sealed class FilePager : IPager
{
    private readonly string _filePath;
    private readonly FileStream _stream;

    private RollbackJournal? _journal;
    private readonly HashSet<uint> _journaledPages = [];

    public FilePager(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _filePath = filePath;
        _stream = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    public bool InTransaction => _journal is not null;

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

    /// <summary>
    /// Checks for a stale journal left by a crashed transaction and rolls back if one is found.
    /// Call this after <see cref="InitializeNew"/> or immediately after opening an existing file.
    /// </summary>
    public void RecoverIfNeeded()
    {
        if (!RollbackJournal.Exists(_filePath))
            return;

        using var journal = RollbackJournal.OpenForRecovery(_filePath);
        ApplyJournalContents(journal);
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

        if (InTransaction)
            JournalHeaderPageIfNeeded();

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

        if (InTransaction)
            JournalPageIfNeeded(page.PageNumber);

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

    public void BeginTransaction()
    {
        if (InTransaction)
            throw new InvalidOperationException("A transaction is already active.");

        var header = ReadDatabaseHeader();
        var originalPageCount = header.PageCount;

        // Snapshot the raw header page bytes for the journal
        var headerBytes = new byte[DbConstants.PageSize];
        _stream.Position = 0;
        _stream.ReadExactly(headerBytes);

        _journal = RollbackJournal.Create(_filePath, originalPageCount, headerBytes);
        _journaledPages.Clear();
        _journaledPages.Add(DbConstants.HeaderPageNumber); // header is already in the journal
    }

    public void Commit()
    {
        if (!InTransaction)
            throw new InvalidOperationException("No active transaction to commit.");

        _stream.Flush(flushToDisk: true);
        _journal!.Delete();
        _journal = null;
        _journaledPages.Clear();
    }

    public void Rollback()
    {
        if (!InTransaction)
            throw new InvalidOperationException("No active transaction to roll back.");

        using var journal = _journal!;
        _journal = null;
        _journaledPages.Clear();

        ApplyJournalContents(journal);
    }

    public void Dispose()
    {
        _journal?.Dispose();
        _stream.Dispose();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private void JournalHeaderPageIfNeeded()
    {
        if (_journaledPages.Contains(DbConstants.HeaderPageNumber))
            return;

        var headerBytes = new byte[DbConstants.PageSize];
        _stream.Position = 0;
        _stream.ReadExactly(headerBytes);
        _journal!.AppendPage(DbConstants.HeaderPageNumber, headerBytes);
        _journaledPages.Add(DbConstants.HeaderPageNumber);
    }

    private void JournalPageIfNeeded(uint pageNumber)
    {
        if (_journaledPages.Contains(pageNumber))
            return;

        // The page may not yet exist (new allocation). Only journal existing pages.
        var header = ReadDatabaseHeader();
        if (pageNumber < header.PageCount)
        {
            var originalBytes = new byte[DbConstants.PageSize];
            _stream.Position = GetPageOffset(pageNumber);
            _stream.ReadExactly(originalBytes);
            _journal!.AppendPage(pageNumber, originalBytes);
        }

        _journaledPages.Add(pageNumber);
    }

    private void ApplyJournalContents(RollbackJournal journal)
    {
        var contents = journal.ReadAll();

        // Restore the header page first (it contains the canonical page count)
        _stream.Position = 0;
        _stream.Write(contents.OriginalHeaderPage);

        // Restore each journaled data page
        foreach (var (pageNumber, bytes) in contents.Pages)
        {
            _stream.Position = GetPageOffset(pageNumber);
            _stream.Write(bytes);
        }

        // Truncate the file back to the original page count so newly-allocated pages disappear
        var originalLength = checked((long)contents.OriginalPageCount * DbConstants.PageSize);
        if (_stream.Length > originalLength)
            _stream.SetLength(originalLength);

        _stream.Flush(flushToDisk: true);

        journal.Delete();
    }

    private void EnsureInitialized()
    {
        if (_stream.Length < DbConstants.PageSize)
        {
            throw new InvalidOperationException("The file is not initialized as a Sqlity database yet.");
        }
    }

    private static long GetPageOffset(uint pageNumber) => checked((long)pageNumber * DbConstants.PageSize);
}
