using System.Buffers.Binary;

namespace Sqlity.Storage.BTree;

/// <summary>
/// A cell stored in an <see cref="IndexLeafPage"/>. The key is a variable-length
/// sort-preserving byte array produced by <see cref="IndexKeyEncoder"/>.
/// On-disk layout: [ushort keyLen][key bytes][int64 primaryKey]
/// </summary>
internal readonly record struct IndexLeafCell(byte[] Key, long PrimaryKey)
{
    public const int FixedOverheadSize = sizeof(ushort) + sizeof(long);

    public int GetRequiredSize() => FixedOverheadSize + Key.Length;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < GetRequiredSize())
            throw new ArgumentException("Destination span is too small for the index leaf cell.", nameof(destination));

        BinaryPrimitives.WriteUInt16LittleEndian(destination[..sizeof(ushort)], checked((ushort)Key.Length));
        Key.CopyTo(destination[sizeof(ushort)..]);
        BinaryPrimitives.WriteInt64LittleEndian(destination[(sizeof(ushort) + Key.Length)..], PrimaryKey);
    }

    public static IndexLeafCell ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < FixedOverheadSize)
            throw new ArgumentException("Source span is too small for an index leaf cell.", nameof(source));

        var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(source[..sizeof(ushort)]);
        var key = source[sizeof(ushort)..(sizeof(ushort) + keyLen)].ToArray();
        var pk = BinaryPrimitives.ReadInt64LittleEndian(source[(sizeof(ushort) + keyLen)..]);
        return new IndexLeafCell(key, pk);
    }
}
