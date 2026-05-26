using System.Buffers.Binary;
using System.Text;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Catalog;

internal sealed class TableSchemaSerializer
{
    private const byte Version1 = 1;
    private const byte CurrentVersion = 2;
    private static readonly Encoding Utf8 = Encoding.UTF8;

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
        if (version != Version1 && version != CurrentVersion)
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

            columns[index] = new ColumnDefinition(columnName, columnType, isNullable);
        }

        return new TableSchema(tableName, columns, primaryKeyOrdinal);
    }

    private static int GetRequiredSize(TableSchema schema)
    {
        var size = sizeof(byte);
        size += sizeof(ushort) + Utf8.GetByteCount(schema.TableName);
        size += sizeof(byte);
        size += sizeof(byte);

        foreach (var column in schema.Columns)
        {
            size += sizeof(ushort) + Utf8.GetByteCount(column.Name);
            size += sizeof(byte); // type
            size += sizeof(byte); // nullable flag
        }

        return size;
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
