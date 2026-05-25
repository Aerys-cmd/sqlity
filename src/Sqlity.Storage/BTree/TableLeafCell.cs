using System.Buffers.Binary;

namespace Sqlity.Storage.BTree;

public readonly record struct TableLeafCell(long PrimaryKey, byte[] Payload)
{
    public int GetRequiredSize() => BTreePageLayout.LeafCellHeaderSize + Payload.Length;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < GetRequiredSize())
        {
            throw new ArgumentException("The destination span is too small for the table leaf cell.", nameof(destination));
        }

        BinaryPrimitives.WriteInt64LittleEndian(destination[..sizeof(long)], PrimaryKey);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[sizeof(long)..(sizeof(long) + sizeof(ushort))], checked((ushort)Payload.Length));
        Payload.CopyTo(destination[BTreePageLayout.LeafCellHeaderSize..]);
    }

    public static TableLeafCell ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < BTreePageLayout.LeafCellHeaderSize)
        {
            throw new ArgumentException("The source span is too small for a table leaf cell.", nameof(source));
        }

        var primaryKey = BinaryPrimitives.ReadInt64LittleEndian(source[..sizeof(long)]);
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(source[sizeof(long)..(sizeof(long) + sizeof(ushort))]);
        var payload = source[BTreePageLayout.LeafCellHeaderSize..(BTreePageLayout.LeafCellHeaderSize + payloadLength)].ToArray();

        return new TableLeafCell(primaryKey, payload);
    }
}
