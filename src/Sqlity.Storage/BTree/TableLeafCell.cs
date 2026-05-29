using System.Buffers.Binary;

namespace Sqlity.Storage.BTree;

public readonly record struct TableLeafCell(long PrimaryKey, byte[] Payload)
{
    /// <summary>
    /// A <see cref="Payload"/> length sentinel stored on-disk when the real payload spills to
    /// overflow pages. <c>0xFFFF</c> is safe because the maximum inline payload for a 4 096-byte
    /// page is well below 65 535 bytes.
    /// </summary>
    public const ushort OverflowSentinel = 0xFFFF;

    /// <summary>
    /// When <see langword="true"/> the cell is an overflow pointer: <see cref="Payload"/> holds
    /// 8 bytes encoding <c>[totalSize: uint32][firstOverflowPageId: uint32]</c>.
    /// Callers of <see cref="BPlusTree"/> never see overflow pointer cells — the tree
    /// materialises them transparently.
    /// </summary>
    public bool IsOverflowPointer { get; init; }

    public int GetRequiredSize() =>
        IsOverflowPointer
            ? BTreePageLayout.LeafCellHeaderSize + BTreePageLayout.OverflowPointerPayloadSize
            : BTreePageLayout.LeafCellHeaderSize + Payload.Length;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < GetRequiredSize())
            throw new ArgumentException("The destination span is too small for the table leaf cell.", nameof(destination));

        BinaryPrimitives.WriteInt64LittleEndian(destination[..sizeof(long)], PrimaryKey);

        if (IsOverflowPointer)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[sizeof(long)..(sizeof(long) + sizeof(ushort))],
                OverflowSentinel);
            Payload.AsSpan().CopyTo(destination[BTreePageLayout.LeafCellHeaderSize..]);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[sizeof(long)..(sizeof(long) + sizeof(ushort))],
                checked((ushort)Payload.Length));
            Payload.AsSpan().CopyTo(destination[BTreePageLayout.LeafCellHeaderSize..]);
        }
    }

    public static TableLeafCell ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < BTreePageLayout.LeafCellHeaderSize)
            throw new ArgumentException("The source span is too small for a table leaf cell.", nameof(source));

        var primaryKey = BinaryPrimitives.ReadInt64LittleEndian(source[..sizeof(long)]);
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(source[sizeof(long)..(sizeof(long) + sizeof(ushort))]);

        if (payloadLength == OverflowSentinel)
        {
            // Overflow pointer cell: read 8 bytes of [totalSize: uint32][firstPageId: uint32].
            var payload = source[BTreePageLayout.LeafCellHeaderSize..(BTreePageLayout.LeafCellHeaderSize + BTreePageLayout.OverflowPointerPayloadSize)].ToArray();
            return new TableLeafCell(primaryKey, payload) { IsOverflowPointer = true };
        }

        var rowPayload = source[BTreePageLayout.LeafCellHeaderSize..(BTreePageLayout.LeafCellHeaderSize + payloadLength)].ToArray();
        return new TableLeafCell(primaryKey, rowPayload);
    }
}
