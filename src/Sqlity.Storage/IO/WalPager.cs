using System.Buffers.Binary;
using Sqlity.Core;
using Sqlity.Storage.Abstractions;
using Sqlity.Storage.Headers;
using Sqlity.Storage.Pages;

namespace Sqlity.Storage.IO;

/// <summary>
/// Write-Ahead Log pager. Writes during an explicit transaction are buffered in memory,
/// then flushed to a WAL sidecar file (<c>&lt;db&gt;.wal</c>) on <see cref="Commit"/>.
/// The WAL is checkpointed into the main database file immediately after being flushed,
/// then deleted. Writes outside an explicit transaction are written directly to disk.
///
/// <para>WAL file format:</para>
/// <list type="bullet">
///   <item>Header: magic <c>"SWLG"</c> (4 B) + committed frame count (uint32 LE, 4 B)</item>
///   <item>N frames: page_number (uint32 LE, 4 B) + page bytes (4096 B)</item>
///   <item>Page number <c>0</c> is a header frame (database header serialized at offset 0).</item>
///   <item>Data frames are written first; the header frame (if any) is last.</item>
///   <item>A <c>frame_count == 0</c> header means the WAL is uncommitted and must be discarded.</item>
/// </list>
/// </summary>
public sealed class WalPager : IPager
{
    private static ReadOnlySpan<byte> Magic => "SWLG"u8;
    private const int WalHeaderSize = 8;                           // magic(4) + frame_count(4)
    private const int FrameSize = sizeof(uint) + DbConstants.PageSize; // page_number(4) + page_bytes(4096)
    private const uint HeaderFramePageNumber = DbConstants.HeaderPageNumber;

    private readonly string _walPath;
    private readonly FileStream _stream;

    private bool _inTransaction;
    private DatabaseHeader? _txHeader;
    private readonly Dictionary<uint, byte[]> _txPages = new();

    public WalPager(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _walPath = dbPath + ".wal";
        _stream = new FileStream(
            dbPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    public bool InTransaction => _inTransaction;

    // ── Initialization ───────────────────────────────────────────────────────

    public void InitializeNew()
    {
        if (_stream.Length != 0)
            throw new InvalidOperationException("A new database can only be initialized on an empty file.");

        var firstPage = new byte[DbConstants.PageSize];
        DatabaseHeader.CreateNew().WriteTo(firstPage);
        _stream.Position = 0;
        _stream.Write(firstPage);
        _stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Checks for a stale committed WAL left by a previous run and checkpoints it into the
    /// main database file. Call this after opening an existing database file.
    /// </summary>
    public void RecoverIfNeeded()
    {
        if (!File.Exists(_walPath))
            return;

        var frames = ReadCommittedWalFrames(_walPath);
        if (frames is null)
        {
            // Uncommitted or corrupt WAL — discard it.
            File.Delete(_walPath);
            return;
        }

        ApplyFramesToDisk(frames);
        _stream.Flush(flushToDisk: true);
        File.Delete(_walPath);
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    public DatabaseHeader ReadDatabaseHeader()
    {
        if (_inTransaction && _txHeader.HasValue)
            return _txHeader.Value;

        return ReadDatabaseHeaderFromDisk();
    }

    public PageBuffer ReadPage(uint pageNumber)
    {
        if (pageNumber == DbConstants.HeaderPageNumber)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Use ReadDatabaseHeader for page 0.");

        var header = ReadDatabaseHeader();
        if (pageNumber >= header.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber),
                $"Page {pageNumber} is outside the database. Page count is {header.PageCount}.");

        if (_inTransaction && _txPages.TryGetValue(pageNumber, out var txBytes))
        {
            var copy = new byte[DbConstants.PageSize];
            txBytes.CopyTo(copy, 0);
            return new PageBuffer(pageNumber, copy);
        }

        return ReadPageFromDisk(pageNumber);
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    public void WriteDatabaseHeader(in DatabaseHeader header)
    {
        if (_inTransaction)
            _txHeader = header;
        else
            WriteDatabaseHeaderToDisk(in header);
    }

    public void WritePage(PageBuffer page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (_inTransaction)
        {
            var bytes = new byte[DbConstants.PageSize];
            page.ReadOnlySpan.CopyTo(bytes);
            _txPages[page.PageNumber] = bytes;
        }
        else
        {
            WritePageToDisk(page);
        }
    }

    // ── Page management (mirrors FilePager) ──────────────────────────────────

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
            WriteDatabaseHeader(header with
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
            throw new ArgumentOutOfRangeException(nameof(pageNumber),
                "The database header page cannot be released.");

        var header = ReadDatabaseHeader();
        if (pageNumber >= header.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber),
                $"Page {pageNumber} is outside the database. Page count is {header.PageCount}.");

        var page = new PageBuffer(pageNumber);
        FreeListPage.Initialize(page, header.FreeListHeadPageId);
        WritePage(page);
        WriteDatabaseHeader(header with
        {
            FreeListHeadPageId = pageNumber,
            FreePageCount = header.FreePageCount + 1
        });
    }

    // ── Transactions ─────────────────────────────────────────────────────────

    public void BeginTransaction()
    {
        if (_inTransaction)
            throw new InvalidOperationException("A transaction is already active.");

        _inTransaction = true;
        _txPages.Clear();
        _txHeader = null;
    }

    public void Commit()
    {
        if (!_inTransaction)
            throw new InvalidOperationException("No active transaction to commit.");

        try
        {
            if (_txPages.Count > 0 || _txHeader.HasValue)
                FlushTransactionToWalAndCheckpoint();
        }
        finally
        {
            _txPages.Clear();
            _txHeader = null;
            _inTransaction = false;
        }
    }

    public void Rollback()
    {
        if (!_inTransaction)
            throw new InvalidOperationException("No active transaction to roll back.");

        _txPages.Clear();
        _txHeader = null;
        _inTransaction = false;
    }

    public void Dispose() => _stream.Dispose();

    // ── WAL write / checkpoint ────────────────────────────────────────────────

    private void FlushTransactionToWalAndCheckpoint()
    {
        FileStream? walStream = null;
        try
        {
            walStream = new FileStream(_walPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            // Step 1: Write WAL header with frame_count=0 (uncommitted marker) and flush.
            Span<byte> walHeader = stackalloc byte[WalHeaderSize];
            Magic.CopyTo(walHeader[..4]);
            BinaryPrimitives.WriteUInt32LittleEndian(walHeader[4..], 0u);
            walStream.Write(walHeader);
            walStream.Flush(flushToDisk: true);

            // Step 2: Append data frames, then header frame.
            var frameBuffer = new byte[FrameSize];
            var frameCount = 0u;

            foreach (var (pageNumber, bytes) in _txPages)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(frameBuffer.AsSpan(0, 4), pageNumber);
                bytes.AsSpan().CopyTo(frameBuffer.AsSpan(4, DbConstants.PageSize));
                walStream.Write(frameBuffer);
                frameCount++;
            }

            byte[]? txHeaderBytes = null;
            if (_txHeader.HasValue)
            {
                txHeaderBytes = new byte[DbConstants.PageSize];
                _txHeader.Value.WriteTo(txHeaderBytes);
                BinaryPrimitives.WriteUInt32LittleEndian(frameBuffer.AsSpan(0, 4), HeaderFramePageNumber);
                txHeaderBytes.AsSpan().CopyTo(frameBuffer.AsSpan(4, DbConstants.PageSize));
                walStream.Write(frameBuffer);
                frameCount++;
            }

            // Step 3: Flush frames, then commit by writing final frame_count.
            walStream.Flush(flushToDisk: true);
            walStream.Position = sizeof(uint); // offset 4: frame_count field
            Span<byte> countBuf = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(countBuf, frameCount);
            walStream.Write(countBuf);
            walStream.Flush(flushToDisk: true);

            // Step 4: Checkpoint — apply data pages, then header.
            foreach (var (pageNumber, bytes) in _txPages)
                WritePageToDisk(new PageBuffer(pageNumber, bytes));

            if (txHeaderBytes is not null)
            {
                var h = DatabaseHeader.ReadFrom(txHeaderBytes);
                WriteDatabaseHeaderToDisk(in h);
            }

            // Step 5: Flush main DB before deleting WAL.
            _stream.Flush(flushToDisk: true);

            // Step 6: Delete WAL — transaction fully committed.
            walStream.Dispose();
            walStream = null;
            File.Delete(_walPath);
        }
        finally
        {
            walStream?.Dispose();
        }
    }

    // ── Recovery helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads a WAL file and returns all frames if it is committed (frame_count &gt; 0 and length
    /// valid), or <see langword="null"/> if the WAL is uncommitted or corrupt.
    /// </summary>
    private static List<(uint PageNumber, byte[] Bytes)>? ReadCommittedWalFrames(string walPath)
    {
        using var walStream = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (walStream.Length < WalHeaderSize)
            return null;

        Span<byte> walHeader = stackalloc byte[WalHeaderSize];
        walStream.ReadExactly(walHeader);

        if (!walHeader[..4].SequenceEqual(Magic))
            return null;

        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(walHeader[4..]);
        if (frameCount == 0)
            return null;

        // Validate: file must be at least as long as expected.
        var expectedLength = (long)WalHeaderSize + (long)frameCount * FrameSize;
        if (walStream.Length < expectedLength)
            return null;

        var frames = new List<(uint, byte[])>((int)frameCount);
        var frameBuffer = new byte[FrameSize];
        for (var i = 0u; i < frameCount; i++)
        {
            walStream.ReadExactly(frameBuffer);
            var pageNumber = BinaryPrimitives.ReadUInt32LittleEndian(frameBuffer.AsSpan(0, 4));
            var bytes = frameBuffer[4..].ToArray();
            frames.Add((pageNumber, bytes));
        }
        return frames;
    }

    private void ApplyFramesToDisk(List<(uint PageNumber, byte[] Bytes)> frames)
    {
        // Apply data pages first, header frame last.
        byte[]? headerBytes = null;

        foreach (var (pn, bytes) in frames)
        {
            if (pn == HeaderFramePageNumber)
                headerBytes = bytes;
            else
                WritePageToDisk(new PageBuffer(pn, bytes));
        }

        if (headerBytes is not null)
        {
            var h = DatabaseHeader.ReadFrom(headerBytes);
            WriteDatabaseHeaderToDisk(in h);
        }
    }

    // ── Low-level disk I/O ───────────────────────────────────────────────────

    private DatabaseHeader ReadDatabaseHeaderFromDisk()
    {
        EnsureInitialized();
        Span<byte> buf = stackalloc byte[DatabaseHeader.Size];
        _stream.Position = 0;
        _stream.ReadExactly(buf);
        return DatabaseHeader.ReadFrom(buf);
    }

    private PageBuffer ReadPageFromDisk(uint pageNumber)
    {
        EnsureInitialized();
        var bytes = new byte[DbConstants.PageSize];
        _stream.Position = GetPageOffset(pageNumber);
        _stream.ReadExactly(bytes, 0, bytes.Length);
        return new PageBuffer(pageNumber, bytes);
    }

    private void WriteDatabaseHeaderToDisk(in DatabaseHeader header)
    {
        EnsureInitialized();
        Span<byte> buf = stackalloc byte[DatabaseHeader.Size];
        header.WriteTo(buf);
        _stream.Position = 0;
        _stream.Write(buf);
        _stream.Flush(flushToDisk: true);
    }

    private void WritePageToDisk(PageBuffer page)
    {
        EnsureInitialized();
        _stream.Position = GetPageOffset(page.PageNumber);
        _stream.Write(page.ReadOnlySpan);
        _stream.Flush(flushToDisk: true);
    }

    private static long GetPageOffset(uint pageNumber) => (long)pageNumber * DbConstants.PageSize;

    private void EnsureInitialized()
    {
        if (_stream.Length == 0)
            throw new InvalidOperationException(
                "The database has not been initialized. Call InitializeNew() first.");
    }
}
