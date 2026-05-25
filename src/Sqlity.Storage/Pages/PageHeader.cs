using System.Buffers.Binary;
using Sqlity.Core;

namespace Sqlity.Storage.Pages;

public readonly record struct PageHeader(
    PageType PageType,
    byte Flags,
    ushort CellCount,
    ushort FreeBlockOffset,
    ushort CellContentStart,
    uint PageNumber,
    uint SpecialPageId,
    ushort FragmentedFreeBytes)
{
    public const int Size = 20;

    public static PageHeader Create(uint pageNumber, PageType pageType) =>
        new(
            pageType,
            0,
            0,
            0,
            checked((ushort)DbConstants.PageSize),
            pageNumber,
            0,
            0);

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"Page header requires at least {Size} bytes.", nameof(destination));
        }

        destination[..Size].Clear();
        destination[0] = (byte)PageType;
        destination[1] = Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..4], CellCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], FreeBlockOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], CellContentStart);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], PageNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], SpecialPageId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..18], FragmentedFreeBytes);
    }

    public static PageHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException($"Page header requires at least {Size} bytes.", nameof(source));
        }

        return new PageHeader(
            (PageType)source[0],
            source[1],
            BinaryPrimitives.ReadUInt16LittleEndian(source[2..4]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[16..18]));
    }
}
