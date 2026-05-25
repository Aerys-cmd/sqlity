using Sqlity.Storage;
using Sqlity.Storage.Catalog;
using Sqlity.Storage.Rows;

namespace Sqlity.Query;

public sealed class QueryEngine : IDisposable
{
    private readonly StorageEngine _storage;
    private readonly bool _ownsStorage;

    public QueryEngine(StorageEngine storage)
        : this(storage, ownsStorage: false)
    {
    }

    public QueryEngine(string filePath)
        : this(StorageEngine.Open(filePath), ownsStorage: true)
    {
    }

    private QueryEngine(StorageEngine storage, bool ownsStorage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        _storage = storage;
        _ownsStorage = ownsStorage;
    }

    public QueryExecutionResult Execute(string sql)
    {
        var statement = new SqlParser(sql).ParseStatement();

        return statement switch
        {
            CreateTableStatement createTable => ExecuteCreateTable(createTable),
            InsertStatement insert => ExecuteInsert(insert),
            SelectStatement select => ExecuteSelect(select),
            _ => throw new InvalidOperationException($"Unsupported statement type {statement.GetType().Name}.")
        };
    }

    public void Dispose()
    {
        if (_ownsStorage)
        {
            _storage.Dispose();
        }
    }

    private QueryExecutionResult ExecuteCreateTable(CreateTableStatement statement)
    {
        if (statement.Columns.Count == 0)
        {
            throw new InvalidOperationException("CREATE TABLE requires at least one column.");
        }

        var columns = new ColumnDefinition[statement.Columns.Count];
        var primaryKeyOrdinal = -1;

        for (var index = 0; index < statement.Columns.Count; index++)
        {
            var column = statement.Columns[index];
            columns[index] = new ColumnDefinition(column.Name, ResolveColumnType(column.TypeName));

            if (column.IsPrimaryKey)
            {
                if (primaryKeyOrdinal >= 0)
                {
                    throw new InvalidOperationException("CREATE TABLE only supports one PRIMARY KEY column.");
                }

                primaryKeyOrdinal = index;
            }
        }

        if (primaryKeyOrdinal < 0)
        {
            throw new InvalidOperationException("CREATE TABLE requires an inline PRIMARY KEY column.");
        }

        _storage.CreateTable(new TableSchema(statement.TableName, columns, primaryKeyOrdinal));
        return QueryExecutionResult.Empty(rowsAffected: 0);
    }

    private QueryExecutionResult ExecuteInsert(InsertStatement statement)
    {
        var table = _storage.GetTable(statement.TableName);
        var values = BindInsertValues(table, statement);
        _storage.Insert(table.TableName, values);
        return QueryExecutionResult.Empty(rowsAffected: 1);
    }

    private QueryExecutionResult ExecuteSelect(SelectStatement statement)
    {
        var table = _storage.GetTable(statement.TableName);
        var selectedOrdinals = BindSelectedColumns(table.Schema, statement.Columns);
        var rows = statement.Filter is null
            ? _storage.ReadAll(table.TableName)
            : ReadFilteredRows(table, statement.Filter);

        var projectedRows = rows
            .Select(row => selectedOrdinals.Select(ordinal => row[ordinal]).ToArray())
            .ToArray();
        var projectedColumns = selectedOrdinals.Select(ordinal => table.Schema.Columns[ordinal].Name).ToArray();

        return QueryExecutionResult.WithRows(projectedColumns, projectedRows);
    }

    private object?[] BindInsertValues(TableInfo table, InsertStatement statement)
    {
        if (statement.Columns is null)
        {
            if (statement.Values.Count != table.Schema.Columns.Count)
            {
                throw new InvalidOperationException($"INSERT INTO '{table.TableName}' requires {table.Schema.Columns.Count} values.");
            }

            return statement.Values
                .Select((literal, index) => ConvertLiteral(table.Schema.Columns[index], literal))
                .ToArray();
        }

        if (statement.Columns.Count != statement.Values.Count)
        {
            throw new InvalidOperationException("INSERT column and value counts must match.");
        }

        var boundValues = new object?[table.Schema.Columns.Count];
        var assignedColumns = new bool[table.Schema.Columns.Count];

        for (var index = 0; index < statement.Columns.Count; index++)
        {
            var columnName = statement.Columns[index];
            var ordinal = table.Schema.GetColumnOrdinal(columnName);
            if (assignedColumns[ordinal])
            {
                throw new InvalidOperationException($"Column '{columnName}' is specified more than once in the INSERT statement.");
            }

            boundValues[ordinal] = ConvertLiteral(table.Schema.Columns[ordinal], statement.Values[index]);
            assignedColumns[ordinal] = true;
        }

        if (assignedColumns.Any(assigned => !assigned))
        {
            throw new InvalidOperationException("INSERT must provide values for every column because NULL is not supported yet.");
        }

        return boundValues;
    }

    private static int[] BindSelectedColumns(TableSchema schema, IReadOnlyList<string>? columns)
    {
        if (columns is null)
        {
            return Enumerable.Range(0, schema.Columns.Count).ToArray();
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException("SELECT requires at least one projected column.");
        }

        return columns.Select(schema.GetColumnOrdinal).ToArray();
    }

    private IReadOnlyList<object?[]> ReadFilteredRows(TableInfo table, PrimaryKeyFilter filter)
    {
        var filterOrdinal = table.Schema.GetColumnOrdinal(filter.ColumnName);
        if (filterOrdinal != table.Schema.PrimaryKeyOrdinal)
        {
            throw new InvalidOperationException($"WHERE only supports the primary key column '{table.Schema.PrimaryKeyColumn.Name}'.");
        }

        var primaryKey = ConvertLiteral(table.Schema.PrimaryKeyColumn, filter.Value);
        if (primaryKey is not long primaryKeyValue)
        {
            throw new InvalidOperationException("Primary key filters must resolve to Int64 values.");
        }

        return _storage.TryReadByPrimaryKey(table.TableName, primaryKeyValue, out var row)
            ? new[] { row! }
            : Array.Empty<object?[]>();
    }

    private static object ConvertLiteral(ColumnDefinition column, SqlLiteral literal) =>
        column.Type switch
        {
            ColumnType.Int64 when literal.Value is long longValue => longValue,
            ColumnType.String when literal.Value is string stringValue => stringValue,
            ColumnType.Blob when literal.Value is byte[] blobValue => blobValue,
            ColumnType.Boolean when literal.Value is bool boolValue => boolValue,
            _ => throw new InvalidOperationException($"Value '{literal.Value}' is not valid for column '{column.Name}' of type {column.Type}.")
        };

    private static ColumnType ResolveColumnType(string typeName) =>
        typeName.ToUpperInvariant() switch
        {
            "INT64" or "INTEGER" or "BIGINT" => ColumnType.Int64,
            "STRING" or "TEXT" => ColumnType.String,
            "BLOB" => ColumnType.Blob,
            "BOOLEAN" or "BOOL" => ColumnType.Boolean,
            _ => throw new InvalidOperationException($"Unsupported SQL column type '{typeName}'.")
        };
}
