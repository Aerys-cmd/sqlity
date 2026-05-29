using System.Buffers.Binary;
using System.Text;

namespace Sqlity.Storage.Catalog;

/// <summary>
/// Serializes and deserializes the per-column NDV (number of distinct values) payload
/// stored in <c>__sqlity_stat1.stat_blob</c>.
/// </summary>
/// <remarks>
/// Wire format (all values little-endian):
/// <code>
/// [version: byte = 1]
/// [columnCount: ushort]
/// For each column:
///   [nameLength: ushort]
///   [name: UTF-8 bytes × nameLength]
///   [ndv: int64]
/// </code>
/// </remarks>
internal sealed class StatsMetadataSerializer
{
    private const byte BlobVersion = 1;
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public byte[] Serialize(IReadOnlyDictionary<string, long> columnNdv)
    {
        ArgumentNullException.ThrowIfNull(columnNdv);

        // Calculate exact size first.
        var size = sizeof(byte) + sizeof(ushort); // version + columnCount
        foreach (var key in columnNdv.Keys)
            size += sizeof(ushort) + Utf8.GetByteCount(key) + sizeof(long);

        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = BlobVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, sizeof(ushort)), checked((ushort)columnNdv.Count));
        offset += sizeof(ushort);

        foreach (var (name, ndv) in columnNdv)
        {
            var nameBytes = Utf8.GetByteCount(name);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, sizeof(ushort)), checked((ushort)nameBytes));
            offset += sizeof(ushort);
            offset += Utf8.GetBytes(name, buffer.AsSpan(offset, nameBytes));
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, sizeof(long)), ndv);
            offset += sizeof(long);
        }

        return buffer;
    }

    public IReadOnlyDictionary<string, long> Deserialize(ReadOnlySpan<byte> source)
    {
        if (source.Length < sizeof(byte) + sizeof(ushort))
            throw new InvalidDataException("Stats blob is too small to contain a valid header.");

        var offset = 0;

        var version = source[offset++];
        if (version != BlobVersion)
            throw new InvalidDataException($"Unsupported stats blob version {version}; expected {BlobVersion}.");

        var columnCount = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..(offset + sizeof(ushort))]);
        offset += sizeof(ushort);

        var result = new Dictionary<string, long>(columnCount, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < columnCount; i++)
        {
            if (offset + sizeof(ushort) > source.Length)
                throw new InvalidDataException("Stats blob ended before column name length.");

            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..(offset + sizeof(ushort))]);
            offset += sizeof(ushort);

            if (offset + nameLen + sizeof(long) > source.Length)
                throw new InvalidDataException("Stats blob ended before column name payload or NDV value.");

            var name = Utf8.GetString(source[offset..(offset + nameLen)]);
            offset += nameLen;

            var ndv = BinaryPrimitives.ReadInt64LittleEndian(source[offset..(offset + sizeof(long))]);
            offset += sizeof(long);

            result[name] = ndv;
        }

        if (offset != source.Length)
            throw new InvalidDataException($"Stats blob has {source.Length - offset} unexpected trailing byte(s).");

        return result;
    }
}
