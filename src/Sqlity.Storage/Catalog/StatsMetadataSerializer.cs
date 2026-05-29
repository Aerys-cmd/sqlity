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
/// Version 1 (legacy):
/// [version: byte = 1]
/// [columnCount: ushort]
/// For each column:
///   [nameLength: ushort]
///   [name: UTF-8 bytes × nameLength]
///   [ndv: int64]
///
/// Version 2 (current):
/// [version: byte = 2]
/// [columnCount: ushort]
/// For each column:
///   [nameLength: ushort]
///   [name: UTF-8 bytes × nameLength]
///   [ndv: int64]
/// [mutations_since_analyze: int64]   ← appended after NDV section
/// </code>
/// Version 1 blobs are still readable; <c>MutationsSinceAnalyze</c> defaults to 0.
/// New writes always emit version 2.
/// </remarks>
internal sealed class StatsMetadataSerializer
{
    private const byte BlobVersion = 2;
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public byte[] Serialize(IReadOnlyDictionary<string, long> columnNdv, long mutationsSinceAnalyze = 0)
    {
        ArgumentNullException.ThrowIfNull(columnNdv);

        // Calculate exact size first.
        var size = sizeof(byte) + sizeof(ushort); // version + columnCount
        foreach (var key in columnNdv.Keys)
            size += sizeof(ushort) + Utf8.GetByteCount(key) + sizeof(long);
        size += sizeof(long); // mutations_since_analyze

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

        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, sizeof(long)), mutationsSinceAnalyze);

        return buffer;
    }

    /// <summary>
    /// Deserializes a stats blob, returning the per-column NDV map and the
    /// accumulated mutation count since the last analyze.
    /// </summary>
    public (IReadOnlyDictionary<string, long> ColumnNdv, long MutationsSinceAnalyze) Deserialize(ReadOnlySpan<byte> source)
    {
        if (source.Length < sizeof(byte) + sizeof(ushort))
            throw new InvalidDataException("Stats blob is too small to contain a valid header.");

        var offset = 0;

        var version = source[offset++];
        if (version != 1 && version != BlobVersion)
            throw new InvalidDataException($"Unsupported stats blob version {version}; expected 1 or {BlobVersion}.");

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

        // v1 blobs have no trailing mutations field.
        if (version == 1)
        {
            if (offset != source.Length)
                throw new InvalidDataException($"Stats blob (v1) has {source.Length - offset} unexpected trailing byte(s).");
            return (result, 0L);
        }

        // v2: read trailing mutations_since_analyze.
        if (offset + sizeof(long) > source.Length)
            throw new InvalidDataException("Stats blob (v2) ended before mutations_since_analyze field.");

        var mutations = BinaryPrimitives.ReadInt64LittleEndian(source[offset..(offset + sizeof(long))]);
        offset += sizeof(long);

        if (offset != source.Length)
            throw new InvalidDataException($"Stats blob (v2) has {source.Length - offset} unexpected trailing byte(s).");

        return (result, mutations);
    }
}
