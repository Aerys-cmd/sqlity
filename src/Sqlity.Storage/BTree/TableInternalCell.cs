using System.Buffers.Binary;

namespace Sqlity.Storage.BTree;

public readonly record struct TableInternalCell(long DividerKey, uint RightChildPageId)
{
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < BTreePageLayout.InternalCellSize)
        {
            throw new ArgumentException("The destination span is too small for a table internal cell.", nameof(destination));
        }

        BinaryPrimitives.WriteInt64LittleEndian(destination[..sizeof(long)], DividerKey);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[sizeof(long)..(sizeof(long) + sizeof(uint))], RightChildPageId);
    }

    public static TableInternalCell ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < BTreePageLayout.InternalCellSize)
        {
            throw new ArgumentException("The source span is too small for a table internal cell.", nameof(source));
        }

        var dividerKey = BinaryPrimitives.ReadInt64LittleEndian(source[..sizeof(long)]);
        var rightChildPageId = BinaryPrimitives.ReadUInt32LittleEndian(source[sizeof(long)..(sizeof(long) + sizeof(uint))]);

        return new TableInternalCell(dividerKey, rightChildPageId);
    }
}
