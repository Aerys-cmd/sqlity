using System.Buffers.Binary;

namespace Sqlity.Storage.BTree;

/// <summary>
/// A cell stored in an <see cref="IndexInternalPage"/>. Divider key is variable-length.
/// On-disk layout: [ushort keyLen][key bytes][uint rightChildPageId]
/// </summary>
internal readonly record struct IndexInternalCell(byte[] DividerKey, uint RightChildPageId)
{
    public const int FixedOverheadSize = sizeof(ushort) + sizeof(uint);

    public int GetRequiredSize() => FixedOverheadSize + DividerKey.Length;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < GetRequiredSize())
            throw new ArgumentException("Destination span is too small for the index internal cell.", nameof(destination));

        BinaryPrimitives.WriteUInt16LittleEndian(destination[..sizeof(ushort)], checked((ushort)DividerKey.Length));
        DividerKey.CopyTo(destination[sizeof(ushort)..]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(sizeof(ushort) + DividerKey.Length)..], RightChildPageId);
    }

    public static IndexInternalCell ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < FixedOverheadSize)
            throw new ArgumentException("Source span is too small for an index internal cell.", nameof(source));

        var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(source[..sizeof(ushort)]);
        var key = source[sizeof(ushort)..(sizeof(ushort) + keyLen)].ToArray();
        var childPageId = BinaryPrimitives.ReadUInt32LittleEndian(source[(sizeof(ushort) + keyLen)..]);
        return new IndexInternalCell(key, childPageId);
    }
}
