using System.Buffers.Binary;
using System.Text;

namespace Sqlity.Storage.Rows;

public sealed class RowSerializer
{
    public const byte CurrentFormatVersion = 1;
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public int GetRequiredSize(TableSchema schema, IReadOnlyList<object?> values)
    {
        ValidateValueCount(schema, values);

        var size = sizeof(byte) + sizeof(byte);
        for (var index = 0; index < schema.Columns.Count; index++)
        {
            size += sizeof(byte) + sizeof(ushort);
            size += GetPayloadLength(schema.Columns[index].Type, values[index]);
        }

        return size;
    }

    public int Write(TableSchema schema, IReadOnlyList<object?> values, Span<byte> destination)
    {
        ValidateValueCount(schema, values);

        var requiredSize = GetRequiredSize(schema, values);
        if (destination.Length < requiredSize)
        {
            throw new ArgumentException("The destination span is too small for the row.", nameof(destination));
        }

        var offset = 0;
        destination[offset++] = CurrentFormatVersion;
        destination[offset++] = checked((byte)schema.Columns.Count);

        for (var index = 0; index < schema.Columns.Count; index++)
        {
            var column = schema.Columns[index];
            var value = values[index];

            if (value is null)
            {
                destination[offset++] = (byte)ColumnType.Null;
                BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..(offset + sizeof(ushort))], 0);
                offset += sizeof(ushort);
            }
            else
            {
                var payloadLength = GetPayloadLength(column.Type, value);
                destination[offset++] = (byte)column.Type;
                BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..(offset + sizeof(ushort))], checked((ushort)payloadLength));
                offset += sizeof(ushort);
                offset += WritePayload(column.Type, value, destination[offset..]);
            }
        }

        return offset;
    }

    public object?[] Read(TableSchema schema, ReadOnlySpan<byte> source)
    {
        if (source.Length < sizeof(byte) + sizeof(byte))
        {
            throw new ArgumentException("The source span is too small for a row.", nameof(source));
        }

        var offset = 0;
        var version = source[offset++];
        if (version != CurrentFormatVersion)
        {
            throw new InvalidDataException($"Unsupported row format version {version}.");
        }

        var columnCount = source[offset++];
        if (columnCount != schema.Columns.Count)
        {
            throw new InvalidDataException($"Expected {schema.Columns.Count} columns, but found {columnCount}.");
        }

        var values = new object?[columnCount];
        for (var index = 0; index < columnCount; index++)
        {
            var expectedType = schema.Columns[index].Type;
            var storedType = (ColumnType)source[offset++];

            var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..(offset + sizeof(ushort))]);
            offset += sizeof(ushort);

            if (storedType == ColumnType.Null)
            {
                values[index] = null;
            }
            else
            {
                if (storedType != expectedType)
                {
                    throw new InvalidDataException($"Expected column type {expectedType}, but found {storedType}.");
                }

                values[index] = ReadPayload(storedType, payloadLength, source[offset..]);
                offset += payloadLength;
            }
        }

        return values;
    }

    private static int GetPayloadLength(ColumnType type, object? value)
    {
        if (value is null) return 0;

        return type switch
        {
            ColumnType.Int64 => value is long ? sizeof(long) : throw new InvalidOperationException("Int64 columns require long values."),
            ColumnType.String => value is string stringValue ? Utf8.GetByteCount(stringValue) : throw new InvalidOperationException("String columns require string values."),
            ColumnType.Blob => value is byte[] blobValue ? blobValue.Length : throw new InvalidOperationException("Blob columns require byte[] values."),
            ColumnType.Boolean => value is bool ? sizeof(byte) : throw new InvalidOperationException("Boolean columns require bool values."),
            _ => throw new NotSupportedException($"Column type {type} is not supported.")
        };
    }

    private static int WritePayload(ColumnType type, object? value, Span<byte> destination) =>
        type switch
        {
            ColumnType.Int64 => WriteInt64((long)value!, destination),
            ColumnType.String => WriteString((string)value!, destination),
            ColumnType.Blob => WriteBlob((byte[])value!, destination),
            ColumnType.Boolean => WriteBoolean((bool)value!, destination),
            _ => throw new NotSupportedException($"Column type {type} is not supported.")
        };

    private static object ReadPayload(ColumnType type, int payloadLength, ReadOnlySpan<byte> source) =>
        type switch
        {
            ColumnType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(source[..sizeof(long)]),
            ColumnType.String => Utf8.GetString(source[..payloadLength]),
            ColumnType.Blob => source[..payloadLength].ToArray(),
            ColumnType.Boolean => source[0] != 0,
            _ => throw new NotSupportedException($"Column type {type} is not supported.")
        };

    private static int WriteInt64(long value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination[..sizeof(long)], value);
        return sizeof(long);
    }

    private static int WriteString(string value, Span<byte> destination) => Utf8.GetBytes(value, destination);

    private static int WriteBlob(byte[] value, Span<byte> destination)
    {
        value.CopyTo(destination);
        return value.Length;
    }

    private static int WriteBoolean(bool value, Span<byte> destination)
    {
        destination[0] = value ? (byte)1 : (byte)0;
        return sizeof(byte);
    }

    private static void ValidateValueCount(TableSchema schema, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count != schema.Columns.Count)
        {
            throw new ArgumentException($"Expected {schema.Columns.Count} values, but received {values.Count}.", nameof(values));
        }
    }
}
