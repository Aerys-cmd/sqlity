using System.Buffers.Binary;
using Sqlity.Core;

namespace Sqlity.Storage.IO;

/// <summary>
/// Manages the rollback journal sidecar file (<c>&lt;db&gt;.journal</c>).
///
/// Format:
/// <list type="bullet">
///   <item>4 bytes – magic "SJRL"</item>
///   <item>4 bytes – original page count (uint32, little-endian)</item>
///   <item>4096 bytes – original header page (page 0) snapshot</item>
///   <item>Repeating page records: 4 bytes page number + 4096 bytes original page bytes</item>
/// </list>
///
/// Protocol:
/// <list type="bullet">
///   <item>Open the journal on <c>BeginTransaction</c>.</item>
///   <item>Call <c>AppendPage</c> before overwriting a page for the first time in a transaction.</item>
///   <item>Call <c>Delete</c> on <c>Commit</c>.</item>
///   <item>Call <c>RestoreAll</c> followed by <c>Delete</c> on <c>Rollback</c>.</item>
///   <item>If the journal exists when the database is opened, roll back automatically.</item>
/// </list>
/// </summary>
internal sealed class RollbackJournal : IDisposable
{
    private static ReadOnlySpan<byte> Magic => "SJRL"u8;

    private const int HeaderSize = 4 + 4 + DbConstants.PageSize; // magic + page count + header page
    private const int PageRecordSize = 4 + DbConstants.PageSize;  // page number + page bytes

    private readonly string _journalPath;
    private FileStream? _stream;

    private RollbackJournal(string journalPath)
    {
        _journalPath = journalPath;
    }

    public static string JournalPath(string dbFilePath) => dbFilePath + ".journal";

    public static bool Exists(string dbFilePath) => File.Exists(JournalPath(dbFilePath));

    /// <summary>Creates and opens a new journal file, writing the header record.</summary>
    public static RollbackJournal Create(string dbFilePath, uint originalPageCount, ReadOnlySpan<byte> originalHeaderPage)
    {
        var journal = new RollbackJournal(JournalPath(dbFilePath));
        journal._stream = new FileStream(
            journal._journalPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None);

        var header = new byte[HeaderSize];
        Magic.CopyTo(header.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), originalPageCount);
        originalHeaderPage.CopyTo(header.AsSpan(8, DbConstants.PageSize));

        journal._stream.Write(header);
        journal._stream.Flush(flushToDisk: true);

        return journal;
    }

    /// <summary>Opens an existing journal file for recovery.</summary>
    public static RollbackJournal OpenForRecovery(string dbFilePath)
    {
        var journal = new RollbackJournal(JournalPath(dbFilePath));
        journal._stream = new FileStream(
            journal._journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        return journal;
    }

    /// <summary>Appends the original content of a page before it is overwritten.</summary>
    public void AppendPage(uint pageNumber, ReadOnlySpan<byte> originalBytes)
    {
        if (_stream is null)
            throw new InvalidOperationException("Journal is not open.");

        var record = new byte[PageRecordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0, 4), pageNumber);
        originalBytes.CopyTo(record.AsSpan(4, DbConstants.PageSize));

        _stream.Seek(0, SeekOrigin.End);
        _stream.Write(record);
        _stream.Flush(flushToDisk: true);
    }

    /// <summary>Reads the original page count and all journaled page records.</summary>
    public JournalContents ReadAll()
    {
        if (_stream is null)
            throw new InvalidOperationException("Journal is not open.");

        _stream.Position = 0;

        // Validate magic
        Span<byte> magic = stackalloc byte[4];
        _stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidOperationException("Journal file is corrupt: invalid magic bytes.");

        // Original page count
        Span<byte> countBytes = stackalloc byte[4];
        _stream.ReadExactly(countBytes);
        var originalPageCount = BinaryPrimitives.ReadUInt32LittleEndian(countBytes);

        // Original header page
        var headerPage = new byte[DbConstants.PageSize];
        _stream.ReadExactly(headerPage);

        // Page records
        var pages = new List<(uint PageNumber, byte[] Bytes)>();
        var recordBuf = new byte[PageRecordSize];
        while (_stream.Position < _stream.Length)
        {
            _stream.ReadExactly(recordBuf);
            var pageNumber = BinaryPrimitives.ReadUInt32LittleEndian(recordBuf.AsSpan(0, 4));
            var pageBytes = recordBuf[4..].ToArray();
            pages.Add((pageNumber, pageBytes));
        }

        return new JournalContents(originalPageCount, headerPage, pages);
    }

    /// <summary>Closes and deletes the journal file.</summary>
    public void Delete()
    {
        _stream?.Dispose();
        _stream = null;

        if (File.Exists(_journalPath))
            File.Delete(_journalPath);
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}

internal sealed record JournalContents(
    uint OriginalPageCount,
    byte[] OriginalHeaderPage,
    IReadOnlyList<(uint PageNumber, byte[] Bytes)> Pages);
