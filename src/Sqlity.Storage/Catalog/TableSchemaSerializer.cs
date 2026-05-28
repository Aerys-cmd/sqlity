using System.Buffers.Binary;
using System.Text;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Catalog;

internal sealed class TableSchemaSerializer
{
    private const byte Version1 = 1;
    private const byte Version2 = 2;
    private const byte CurrentVersion = 3;
    private static readonly Encoding Utf8 = Encoding.UTF8;

    // Default value type tags used in Version 3 serialisation.
    private const byte DefaultTagNone = 0;
    private const byte DefaultTagNull = 1;
    private const byte DefaultTagInt64 = 2;
    private const byte DefaultTagString = 3;
    private const byte DefaultTagFloat64 = 4;
    private const byte DefaultTagBoolean = 5;

    public byte[] Serialize(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var requiredSize = GetRequiredSize(schema);
        var buffer = new byte[requiredSize];
        var offset = 0;

        buffer[offset++] = CurrentVersion;
        offset = WriteString(schema.TableName, buffer, offset);
        buffer[offset++] = checked((byte)schema.PrimaryKeyOrdinal);
        buffer[offset++] = checked((byte)schema.Columns.Count);

        foreach (var column in schema.Columns)
        {
            offset = WriteString(column.Name, buffer, offset);
            buffer[offset++] = (byte)column.Type;
            buffer[offset++] = column.IsNullable ? (byte)1 : (byte)0;
            buffer[offset++] = column.IsAutoIncrement ? (byte)1 : (byte)0;
            offset = WriteDefaultValue(column, buffer, offset);
        }

        return buffer;
    }

    public TableSchema Deserialize(ReadOnlySpan<byte> source)
    {
        if (source.Length < 4)
        {
            throw new InvalidDataException("The table schema payload is too small.");
        }

        var offset = 0;
        var version = source[offset++];
        if (version != Version1 && version != Version2 && version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported table schema format version {version}.");
        }

        var tableName = ReadString(source, ref offset);
        var primaryKeyOrdinal = source[offset++];
        var columnCount = source[offset++];
        if (columnCount == 0)
        {
            throw new InvalidDataException("A persisted table schema must contain at least one column.");
        }

        var columns = new ColumnDefinition[columnCount];
        for (var index = 0; index < columnCount; index++)
        {
            var columnName = ReadString(source, ref offset);
            if (offset >= source.Length)
            {
                throw new InvalidDataException("The persisted table schema ended before the column type was available.");
            }

            var columnType = (ColumnType)source[offset++];

            bool isNullable;
            if (version == Version1)
            {
                // Version 1 had no nullable flag; all non-PK columns default to nullable.
                isNullable = index != primaryKeyOrdinal;
            }
            else
            {
                if (offset >= source.Length)
                {
                    throw new InvalidDataException("The persisted table schema ended before the nullable flag was available.");
                }

                isNullable = source[offset++] != 0;
            }

            bool isAutoIncrement = false;
            bool hasDefault = false;
            object? defaultValue = null;

            if (version == CurrentVersion)
            {
                if (offset >= source.Length)
                    throw new InvalidDataException("The persisted table schema ended before the auto-increment flag was available.");

                isAutoIncrement = source[offset++] != 0;
                (hasDefault, defaultValue) = ReadDefaultValue(source, ref offset);
            }

            columns[index] = new ColumnDefinition(columnName, columnType, isNullable, hasDefault, defaultValue, isAutoIncrement);
        }

        return new TableSchema(tableName, columns, primaryKeyOrdinal);
    }

    private static int GetRequiredSize(TableSchema schema)
    {
        var size = sizeof(byte); // version
        size += sizeof(ushort) + Utf8.GetByteCount(schema.TableName);
        size += sizeof(byte); // pk ordinal
        size += sizeof(byte); // column count

        foreach (var column in schema.Columns)
        {
            size += sizeof(ushort) + Utf8.GetByteCount(column.Name);
            size += sizeof(byte); // type
            size += sizeof(byte); // nullable flag
            size += sizeof(byte); // auto-increment flag
            size += GetDefaultValueSize(column); // default value tag + payload
        }

        return size;
    }

    private static int GetDefaultValueSize(ColumnDefinition column)
    {
        if (!column.HasDefault) return 1; // tag only (DefaultTagNone)
        if (column.DefaultValue is null) return 1; // DefaultTagNull
        return column.DefaultValue switch
        {
            long => 1 + sizeof(long),
            double => 1 + sizeof(double),
            bool => 1 + sizeof(byte),
            string s => 1 + sizeof(ushort) + Utf8.GetByteCount(s),
            _ => 1 // unknown → no default
        };
    }

    private static int WriteDefaultValue(ColumnDefinition column, byte[] destination, int offset)
    {
        if (!column.HasDefault)
        {
            destination[offset++] = DefaultTagNone;
            return offset;
        }

        if (column.DefaultValue is null)
        {
            destination[offset++] = DefaultTagNull;
            return offset;
        }

        switch (column.DefaultValue)
        {
            case long longVal:
                destination[offset++] = DefaultTagInt64;
                BinaryPrimitives.WriteInt64LittleEndian(destination.AsSpan(offset, sizeof(long)), longVal);
                offset += sizeof(long);
                break;

            case double doubleVal:
                destination[offset++] = DefaultTagFloat64;
                BinaryPrimitives.WriteDoubleLittleEndian(destination.AsSpan(offset, sizeof(double)), doubleVal);
                offset += sizeof(double);
                break;

            case bool boolVal:
                destination[offset++] = DefaultTagBoolean;
                destination[offset++] = boolVal ? (byte)1 : (byte)0;
                break;

            case string strVal:
                destination[offset++] = DefaultTagString;
                offset = WriteString(strVal, destination, offset);
                break;

            default:
                destination[offset++] = DefaultTagNone;
                break;
        }

        return offset;
    }

    private static (bool HasDefault, object? Value) ReadDefaultValue(ReadOnlySpan<byte> source, ref int offset)
    {
        if (offset >= source.Length)
            throw new InvalidDataException("The persisted table schema ended before the default value tag.");

        var tag = source[offset++];
        return tag switch
        {
            DefaultTagNone => (false, null),
            DefaultTagNull => (true, null),
            DefaultTagInt64 => (true, BinaryPrimitives.ReadInt64LittleEndian(source.Slice(offset, sizeof(long))).AlsoAdvance(sizeof(long), ref offset)),
            DefaultTagFloat64 => (true, BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(offset, sizeof(double))).AlsoAdvance(sizeof(double), ref offset)),
            DefaultTagBoolean => (true, (source[offset++] != 0)),
            DefaultTagString => (true, ReadString(source, ref offset)),
            _ => (false, null)
        };
    }

    private static int WriteString(string value, byte[] destination, int offset)
    {
        var byteCount = Utf8.GetByteCount(value);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, sizeof(ushort)), checked((ushort)byteCount));
        offset += sizeof(ushort);
        offset += Utf8.GetBytes(value, destination.AsSpan(offset, byteCount));
        return offset;
    }

    private static string ReadString(ReadOnlySpan<byte> source, ref int offset)
    {
        if (offset + sizeof(ushort) > source.Length)
        {
            throw new InvalidDataException("The persisted table schema ended before a string length could be read.");
        }

        var byteCount = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..(offset + sizeof(ushort))]);
        offset += sizeof(ushort);

        if (offset + byteCount > source.Length)
        {
            throw new InvalidDataException("The persisted table schema ended before a string payload could be read.");
        }

        var value = Utf8.GetString(source[offset..(offset + byteCount)]);
        offset += byteCount;
        return value;
    }
}

/// <summary>Extension helpers used to combine a value read with an offset advance in a single expression.</summary>
internal static class BinaryReadExtensions
{
    internal static long AlsoAdvance(this long value, int bytes, ref int offset) { offset += bytes; return value; }
    internal static double AlsoAdvance(this double value, int bytes, ref int offset) { offset += bytes; return value; }
}
