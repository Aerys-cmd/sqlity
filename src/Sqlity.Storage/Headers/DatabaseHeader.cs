using System.Buffers.Binary;
using Sqlity.Core;

namespace Sqlity.Storage.Headers;

public readonly record struct DatabaseHeader(
    uint FormatVersion,
    ushort PageSize,
    uint PageCount,
    uint RootPageId,
    uint FreeListHeadPageId,
    uint FreePageCount,
    uint SchemaVersion)
{
    public const int Size = 64;

    public static DatabaseHeader CreateNew() =>
        new(
            DbConstants.FormatVersion,
            checked((ushort)DbConstants.PageSize),
            1,
            0,
            0,
            0,
            1);

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"Database header requires at least {Size} bytes.", nameof(destination));
        }

        destination[..Size].Clear();

        DbConstants.Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..14], PageSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..16], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..20], PageCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], RootPageId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..28], FreeListHeadPageId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..32], FreePageCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..36], SchemaVersion);
    }

    public static DatabaseHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException($"Database header requires at least {Size} bytes.", nameof(source));
        }

        if (!source[..DbConstants.Magic.Length].SequenceEqual(DbConstants.Magic))
        {
            throw new InvalidDataException("The file does not start with the Sqlity database magic header.");
        }

        var formatVersion = BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]);
        var pageSize = BinaryPrimitives.ReadUInt16LittleEndian(source[12..14]);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(source[14..16]);
        var pageCount = BinaryPrimitives.ReadUInt32LittleEndian(source[16..20]);
        var rootPageId = BinaryPrimitives.ReadUInt32LittleEndian(source[20..24]);
        var freeListHeadPageId = BinaryPrimitives.ReadUInt32LittleEndian(source[24..28]);
        var freePageCount = BinaryPrimitives.ReadUInt32LittleEndian(source[28..32]);
        var schemaVersion = BinaryPrimitives.ReadUInt32LittleEndian(source[32..36]);

        if (pageSize != DbConstants.PageSize)
        {
            throw new InvalidDataException($"Expected page size {DbConstants.PageSize}, but found {pageSize}.");
        }

        if (headerSize != Size)
        {
            throw new InvalidDataException($"Expected header size {Size}, but found {headerSize}.");
        }

        return new DatabaseHeader(
            formatVersion,
            pageSize,
            pageCount,
            rootPageId,
            freeListHeadPageId,
            freePageCount,
            schemaVersion);
    }
}
