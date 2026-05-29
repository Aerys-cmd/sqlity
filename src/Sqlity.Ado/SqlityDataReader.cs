using System.Data;
using System.Data.Common;
using Sqlity.Query;

namespace Sqlity.Ado;

public sealed class SqlityDataReader : DbDataReader
{
    private readonly QueryExecutionResult _result;
    private readonly Dictionary<string, int> _nameToOrdinal;
    private int _rowIndex = -1;
    private bool _isClosed;

    public SqlityDataReader(QueryExecutionResult result)
    {
        _result = result;
        _nameToOrdinal = result.Columns
            .Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
    }

    public override int FieldCount => _result.Columns.Count;
    public override bool HasRows => _result.Rows.Count > 0;
    public override int RecordsAffected => _result.RowsAffected;
    public override bool IsClosed => _isClosed;
    public override int Depth => 0;

    public override bool Read()
    {
        if (_isClosed)
            return false;
        _rowIndex++;
        return _rowIndex < _result.Rows.Count;
    }

    public override bool NextResult() => false;

    public override void Close() => _isClosed = true;

    public override string GetName(int ordinal) => _result.Columns[ordinal];

    public override int GetOrdinal(string name) =>
        _nameToOrdinal.TryGetValue(name, out var ordinal)
            ? ordinal
            : throw new IndexOutOfRangeException($"Column '{name}' was not found.");

    public override object GetValue(int ordinal)
    {
        var value = _result.Rows[_rowIndex][ordinal];
        return value ?? DBNull.Value;
    }

    public override bool IsDBNull(int ordinal) =>
        _result.Rows[_rowIndex][ordinal] is null;

    public override string GetString(int ordinal) =>
        (string)_result.Rows[_rowIndex][ordinal]!;

    public override long GetInt64(int ordinal) =>
        (long)_result.Rows[_rowIndex][ordinal]!;

    public override int GetInt32(int ordinal) =>
        Convert.ToInt32(GetInt64(ordinal));

    public override bool GetBoolean(int ordinal) =>
        (bool)_result.Rows[_rowIndex][ordinal]!;

    public override byte GetByte(int ordinal) =>
        Convert.ToByte(GetInt64(ordinal));

    public override char GetChar(int ordinal) =>
        GetString(ordinal)[0];

    public override short GetInt16(int ordinal) =>
        Convert.ToInt16(GetInt64(ordinal));

    public override float GetFloat(int ordinal) =>
        Convert.ToSingle(_result.Rows[_rowIndex][ordinal]!);

    public override double GetDouble(int ordinal) =>
        Convert.ToDouble(_result.Rows[_rowIndex][ordinal]!);

    public override decimal GetDecimal(int ordinal) =>
        Convert.ToDecimal(_result.Rows[_rowIndex][ordinal]!);

    public override DateTime GetDateTime(int ordinal) =>
        (DateTime)_result.Rows[_rowIndex][ordinal]!;

    public override Guid GetGuid(int ordinal) =>
        (Guid)_result.Rows[_rowIndex][ordinal]!;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = GetValue(i);
        return count;
    }

    public override Type GetFieldType(int ordinal)
    {
        if (_result.ColumnTypes.Count > 0)
            return TypeMap.ToClrType(_result.ColumnTypes[ordinal]);

        // Fallback: sniff type from first non-null row value.
        foreach (var row in _result.Rows)
        {
            if (row[ordinal] is not null)
                return row[ordinal]!.GetType();
        }
        return typeof(object);
    }

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var blob = (byte[])_result.Rows[_rowIndex][ordinal]!;
        if (buffer is null)
            return blob.Length;
        var count = Math.Min(length, blob.Length - (int)dataOffset);
        Array.Copy(blob, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var str = GetString(ordinal);
        if (buffer is null)
            return str.Length;
        var count = Math.Min(length, str.Length - (int)dataOffset);
        str.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override DataTable GetSchemaTable()
    {
        var schemaTable = new DataTable("SchemaTable");

        schemaTable.Columns.Add(SchemaTableColumn.ColumnName,        typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.ColumnOrdinal,     typeof(int));
        schemaTable.Columns.Add(SchemaTableColumn.ColumnSize,        typeof(int));
        schemaTable.Columns.Add(SchemaTableColumn.NumericPrecision,  typeof(short));
        schemaTable.Columns.Add(SchemaTableColumn.NumericScale,      typeof(short));
        schemaTable.Columns.Add(SchemaTableColumn.DataType,          typeof(Type));
        schemaTable.Columns.Add(SchemaTableColumn.ProviderType,      typeof(int));
        schemaTable.Columns.Add(SchemaTableColumn.IsLong,            typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.AllowDBNull,       typeof(bool));
        schemaTable.Columns.Add(SchemaTableOptionalColumn.IsReadOnly,      typeof(bool));
        schemaTable.Columns.Add(SchemaTableOptionalColumn.IsRowVersion,    typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.IsUnique,          typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.IsKey,             typeof(bool));
        schemaTable.Columns.Add(SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool));
        schemaTable.Columns.Add(SchemaTableColumn.BaseColumnName,    typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.BaseSchemaName,    typeof(string));
        schemaTable.Columns.Add(SchemaTableColumn.BaseTableName,     typeof(string));

        for (var i = 0; i < FieldCount; i++)
        {
            var allowNull = _result.ColumnNullables.Count > 0
                ? _result.ColumnNullables[i]
                : true;

            var providerType = _result.ColumnTypes.Count > 0
                ? (int)_result.ColumnTypes[i]
                : 0;

            schemaTable.Rows.Add(
                _result.Columns[i],  // ColumnName
                i,                   // ColumnOrdinal
                -1,                  // ColumnSize — unbounded for strings/blobs
                DBNull.Value,        // NumericPrecision
                DBNull.Value,        // NumericScale
                GetFieldType(i),     // DataType
                providerType,        // ProviderType
                false,               // IsLong
                allowNull,           // AllowDBNull
                true,                // IsReadOnly
                false,               // IsRowVersion
                false,               // IsUnique
                false,               // IsKey
                false,               // IsAutoIncrement
                _result.Columns[i],  // BaseColumnName
                DBNull.Value,        // BaseSchemaName
                DBNull.Value         // BaseTableName
            );
        }

        return schemaTable;
    }


    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name]  => GetValue(GetOrdinal(name));

    public override IEnumerator<IDataRecord> GetEnumerator()
    {
        while (Read())
            yield return this;
    }
}
