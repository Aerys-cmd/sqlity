using System.Buffers.Binary;
using System.Text;

namespace Sqlity.Storage.Catalog;

internal sealed class IndexMetadataSerializer
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    /// <summary>Serializes column names and uniqueness flag into a compact byte blob.</summary>
    public byte[] Serialize(IReadOnlyList<string> columns, bool isUnique)
    {
        var size = sizeof(byte) + sizeof(byte); // isUnique + columnCount
        foreach (var col in columns)
            size += sizeof(ushort) + Utf8.GetByteCount(col);

        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = isUnique ? (byte)1 : (byte)0;
        buffer[offset++] = checked((byte)columns.Count);

        foreach (var col in columns)
        {
            var byteCount = Utf8.GetByteCount(col);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, sizeof(ushort)), checked((ushort)byteCount));
            offset += sizeof(ushort);
            offset += Utf8.GetBytes(col, buffer.AsSpan(offset, byteCount));
        }

        return buffer;
    }

    /// <summary>Deserializes the metadata blob produced by <see cref="Serialize"/>.</summary>
    public (IReadOnlyList<string> Columns, bool IsUnique) Deserialize(ReadOnlySpan<byte> source)
    {
        if (source.Length < 2)
            throw new InvalidDataException("Index metadata blob is too small.");

        var offset = 0;
        var isUnique = source[offset++] != 0;
        var columnCount = source[offset++];

        if (columnCount == 0)
            throw new InvalidDataException("An index must reference at least one column.");

        var columns = new string[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            if (offset + sizeof(ushort) > source.Length)
                throw new InvalidDataException("Index metadata ended before a column name length.");

            var byteCount = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..(offset + sizeof(ushort))]);
            offset += sizeof(ushort);

            if (offset + byteCount > source.Length)
                throw new InvalidDataException("Index metadata ended before a column name payload.");

            columns[i] = Utf8.GetString(source[offset..(offset + byteCount)]);
            offset += byteCount;
        }

        return (columns, isUnique);
    }
}
