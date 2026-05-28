using System.Buffers.Binary;
using System.Text;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.BTree;

/// <summary>
/// Produces sort-preserving byte-array keys suitable for use in <see cref="SecondaryBPlusTree"/>.
/// Comparing two encoded keys with <see cref="MemoryExtensions.SequenceCompareTo"/> yields the
/// same ordering as comparing the original CLR values.
/// </summary>
public static class IndexKeyEncoder
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    /// <summary>
    /// Encodes a multi-column index key. The encoded columns are concatenated in declaration order.
    /// For non-unique indexes, <paramref name="primaryKey"/> is appended so that each entry is unique.
    /// </summary>
    public static byte[] Encode(
        IReadOnlyList<ColumnDefinition> columns,
        IReadOnlyList<int> columnOrdinals,
        object?[] rowValues,
        long? primaryKey = null)
    {
        using var ms = new MemoryStream();

        for (var i = 0; i < columnOrdinals.Count; i++)
        {
            var ordinal = columnOrdinals[i];
            var value = rowValues[ordinal];
            var columnType = columns[ordinal].Type;
            EncodeValue(ms, columnType, value);
        }

        if (primaryKey.HasValue)
        {
            EncodeInt64(ms, primaryKey.Value);
        }

        return ms.ToArray();
    }

    /// <summary>Encodes a single value as a sort-preserving byte sequence.</summary>
    private static void EncodeValue(Stream destination, ColumnType columnType, object? value)
    {
        if (value is null)
        {
            // NULL sorts before any non-null value.
            destination.WriteByte(0x00);
            return;
        }

        // Non-null values get a 0x01 prefix so they always sort after NULL.
        destination.WriteByte(0x01);

        switch (columnType)
        {
            case ColumnType.Int64:
                EncodeInt64(destination, (long)value);
                break;

            case ColumnType.String:
                EncodeString(destination, (string)value);
                break;

            case ColumnType.Boolean:
                destination.WriteByte((bool)value ? (byte)0x01 : (byte)0x00);
                break;

            case ColumnType.Blob:
                var blob = (byte[])value;
                Span<byte> lenBuf = stackalloc byte[sizeof(ushort)];
                BinaryPrimitives.WriteUInt16BigEndian(lenBuf, checked((ushort)blob.Length));
                destination.Write(lenBuf);
                destination.Write(blob);
                break;

            case ColumnType.Float64:
                EncodeFloat64(destination, (double)value);
                break;

            case ColumnType.Date:
                var dateOnly = value is string ds ? DateOnly.Parse(ds) : (DateOnly)value;
                EncodeString(destination, dateOnly.ToString("yyyy-MM-dd"));
                break;

            case ColumnType.DateTime:
                var dateTime = value is string dts ? System.DateTime.Parse(dts, null, System.Globalization.DateTimeStyles.RoundtripKind) : (DateTime)value;
                EncodeString(destination, dateTime.ToString("O"));
                break;

            default:
                throw new NotSupportedException($"Cannot encode column type {columnType} as an index key.");
        }
    }

    /// <summary>
    /// Encodes an Int64 in big-endian order with the sign bit XOR'd so that
    /// negative values sort before zero, and zero sorts before positive values,
    /// consistent with lexicographic byte comparison.
    /// </summary>
    private static void EncodeInt64(Stream destination, long value)
    {
        Span<byte> buf = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buf, value);
        buf[0] ^= 0x80; // flip sign bit
        destination.Write(buf);
    }

    /// <summary>
    /// Encodes a string as UTF-8 bytes terminated by 0x00. Since UTF-8 never
    /// produces a 0x00 byte for any non-NUL character, this is unambiguous and
    /// preserves lexicographic order for ordinal string comparisons.
    /// </summary>
    private static void EncodeString(Stream destination, string value)
    {
        var bytes = Utf8.GetBytes(value);
        destination.Write(bytes);
        destination.WriteByte(0x00); // null terminator as column separator
    }

    /// <summary>
    /// Encodes a double in sort-preserving big-endian form.
    /// For positive values (sign bit = 0), flip only the sign bit so positives
    /// sort after negatives. For negative values (sign bit = 1), flip all bits
    /// so that more-negative values sort before less-negative ones.
    /// </summary>
    private static void EncodeFloat64(Stream destination, double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        // If sign bit is set (negative), flip all bits; otherwise flip only sign bit.
        var encoded = bits < 0 ? ~bits : bits ^ long.MinValue;
        Span<byte> buf = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buf, encoded);
        destination.Write(buf);
    }
}
