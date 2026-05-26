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
            DeleteStatement delete => ExecuteDelete(delete),
            UpdateStatement update => ExecuteUpdate(update),
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
        var fromTable = _storage.GetTable(statement.TableName);

        if (statement.Joins.Count == 0)
        {
            var selectedOrdinals = BindSingleTableColumns(fromTable.Schema, statement.Columns);
            var rows = statement.Filter is null
                ? _storage.ReadAll(statement.TableName)
                : ReadFilteredRows(fromTable, statement.Filter);

            var projectedRows = rows
                .Select(row => selectedOrdinals.Select(i => row[i]).ToArray())
                .ToArray();

            string[] projectedColumns;
            if (statement.Columns is null)
            {
                projectedColumns = selectedOrdinals
                    .Select(i => fromTable.Schema.Columns[i].Name)
                    .ToArray();
            }
            else
            {
                projectedColumns = statement.Columns
                    .Select(col => col.TableName is not null
                        ? $"{col.TableName}.{col.ColumnName}"
                        : col.ColumnName)
                    .ToArray();
            }

            return QueryExecutionResult.WithRows(projectedColumns, projectedRows);
        }

        return ExecuteSelectWithJoins(fromTable, statement);
    }

    private QueryExecutionResult ExecuteSelectWithJoins(TableInfo fromTable, SelectStatement statement)
    {
        var context = BuildJoinContext(fromTable, statement.Joins);
        var totalCols = context.Sum(e => e.Table.Schema.Columns.Count);

        var currentRows = _storage.ReadAll(fromTable.TableName)
            .Select(row =>
            {
                var combined = new object?[totalCols];
                Array.Copy(row, combined, row.Length);
                return combined;
            })
            .ToList();

        foreach (var join in statement.Joins)
        {
            var joinEntry = context.First(e => string.Equals(e.Table.TableName, join.TableName, StringComparison.OrdinalIgnoreCase));
            var rightRows = _storage.ReadAll(join.TableName);

            var leftEntry = FindTableEntry(context, join.Condition.LeftTable);
            var rightEntry = FindTableEntry(context, join.Condition.RightTable);
            var leftColFlat = leftEntry.Offset + leftEntry.Table.Schema.GetColumnOrdinal(join.Condition.LeftColumn);
            var rightColFlat = rightEntry.Offset + rightEntry.Table.Schema.GetColumnOrdinal(join.Condition.RightColumn);

            var nextRows = new List<object?[]>();
            foreach (var leftCombined in currentRows)
            {
                var matched = false;
                foreach (var rightRow in rightRows)
                {
                    var candidate = (object?[])leftCombined.Clone();
                    Array.Copy(rightRow, 0, candidate, joinEntry.Offset, rightRow.Length);
                    if (ValuesEqual(candidate[leftColFlat], candidate[rightColFlat]))
                    {
                        nextRows.Add(candidate);
                        matched = true;
                    }
                }

                if (!matched && join.JoinType == JoinType.Left)
                {
                    nextRows.Add(leftCombined);
                }
            }

            currentRows = nextRows;
        }

        IEnumerable<object?[]> filteredRows = currentRows;
        if (statement.Filter is not null)
        {
            filteredRows = currentRows.Where(row => EvaluateWhereInContext(statement.Filter, row, context));
        }

        var (selectedIndices, outputColumnNames) = BindJoinColumns(statement.Columns, context);

        var projectedRows2 = filteredRows
            .Select(row => selectedIndices.Select(i => row[i]).ToArray())
            .ToArray();

        return QueryExecutionResult.WithRows(outputColumnNames, projectedRows2);
    }

    private QueryExecutionResult ExecuteDelete(DeleteStatement statement)
    {
        var table = _storage.GetTable(statement.TableName);

        if (TryExtractPrimaryKeyEquality(table, statement.Filter, out var pkValue))
        {
            _storage.Delete(table.TableName, pkValue);
            return QueryExecutionResult.Empty(rowsAffected: 1);
        }

        var context = new[] { (Table: table, Offset: 0) };
        var matchingRows = _storage.ReadAll(table.TableName)
            .Where(row => EvaluateWhereInContext(statement.Filter, row, context))
            .ToArray();

        foreach (var row in matchingRows)
        {
            _storage.Delete(table.TableName, (long)row[table.Schema.PrimaryKeyOrdinal]!);
        }

        return QueryExecutionResult.Empty(rowsAffected: matchingRows.Length);
    }

    private QueryExecutionResult ExecuteUpdate(UpdateStatement statement)
    {
        var table = _storage.GetTable(statement.TableName);

        if (TryExtractPrimaryKeyEquality(table, statement.Filter, out var pkValue))
        {
            if (!_storage.TryReadByPrimaryKey(table.TableName, pkValue, out var existingValues) || existingValues is null)
            {
                throw new InvalidOperationException($"Table '{table.TableName}' does not contain a row with primary key {pkValue}.");
            }

            var newValues = ApplyAssignments(table, existingValues, statement.Assignments);
            _storage.Update(table.TableName, pkValue, newValues);
            return QueryExecutionResult.Empty(rowsAffected: 1);
        }

        var context = new[] { (Table: table, Offset: 0) };
        var matchingRows = _storage.ReadAll(table.TableName)
            .Where(row => EvaluateWhereInContext(statement.Filter, row, context))
            .ToArray();

        foreach (var row in matchingRows)
        {
            var pk = (long)row[table.Schema.PrimaryKeyOrdinal]!;
            var updated = ApplyAssignments(table, row, statement.Assignments);
            _storage.Update(table.TableName, pk, updated);
        }

        return QueryExecutionResult.Empty(rowsAffected: matchingRows.Length);
    }

    private object?[] ApplyAssignments(TableInfo table, object?[] existingValues, IReadOnlyList<ColumnAssignment> assignments)
    {
        var newValues = existingValues.ToArray();
        foreach (var assignment in assignments)
        {
            var columnOrdinal = table.Schema.GetColumnOrdinal(assignment.ColumnName);
            if (columnOrdinal == table.Schema.PrimaryKeyOrdinal)
            {
                throw new InvalidOperationException($"Cannot update the primary key column '{table.Schema.PrimaryKeyColumn.Name}'.");
            }

            newValues[columnOrdinal] = ConvertLiteral(table.Schema.Columns[columnOrdinal], assignment.Value);
        }

        return newValues;
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

    private static int[] BindSingleTableColumns(TableSchema schema, IReadOnlyList<ColumnReference>? columns)
    {
        if (columns is null)
        {
            return Enumerable.Range(0, schema.Columns.Count).ToArray();
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException("SELECT requires at least one projected column.");
        }

        return columns.Select(col =>
        {
            if (col.TableName is not null &&
                !string.Equals(col.TableName, schema.TableName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Table '{col.TableName}' not found in the FROM clause.");
            }

            return schema.GetColumnOrdinal(col.ColumnName);
        }).ToArray();
    }

    private IReadOnlyList<object?[]> ReadFilteredRows(TableInfo table, WhereExpression filter)
    {
        if (TryExtractPrimaryKeyEquality(table, filter, out var pkValue))
        {
            return _storage.TryReadByPrimaryKey(table.TableName, pkValue, out var row)
                ? new[] { row! }
                : Array.Empty<object?[]>();
        }

        var context = new[] { (Table: table, Offset: 0) };
        return _storage.ReadAll(table.TableName)
            .Where(row => EvaluateWhereInContext(filter, row, context))
            .ToArray();
    }

    private static bool TryExtractPrimaryKeyEquality(TableInfo table, WhereExpression filter, out long primaryKey)
    {
        primaryKey = 0;
        if (filter is not ComparisonExpression { Op: ComparisonOp.Equals } cmp)
            return false;
        if (cmp.TableName is not null &&
            !string.Equals(cmp.TableName, table.TableName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!table.Schema.TryGetColumnOrdinal(cmp.ColumnName, out var ordinal) ||
            ordinal != table.Schema.PrimaryKeyOrdinal)
            return false;
        if (ConvertLiteral(table.Schema.PrimaryKeyColumn, cmp.Value) is not long pk)
            return false;
        primaryKey = pk;
        return true;
    }

    private static bool EvaluateWhereInContext(
        WhereExpression filter,
        object?[] row,
        IReadOnlyList<(TableInfo Table, int Offset)> context)
    {
        return filter switch
        {
            BinaryLogicalExpression binary => binary.Op == LogicalOp.And
                ? EvaluateWhereInContext(binary.Left, row, context) &&
                  EvaluateWhereInContext(binary.Right, row, context)
                : EvaluateWhereInContext(binary.Left, row, context) ||
                  EvaluateWhereInContext(binary.Right, row, context),

            ComparisonExpression cmp =>
                EvaluateComparison(row[ResolveColumn(cmp.TableName, cmp.ColumnName, context)], cmp.Op, cmp.Value.Value),

            _ => throw new InvalidOperationException($"Unknown WHERE expression type '{filter.GetType().Name}'.")
        };
    }

    private static int ResolveColumn(
        string? tableName,
        string columnName,
        IReadOnlyList<(TableInfo Table, int Offset)> context)
    {
        if (tableName is not null)
        {
            var entry = context.FirstOrDefault(e =>
                string.Equals(e.Table.TableName, tableName, StringComparison.OrdinalIgnoreCase));
            if (entry.Table is null)
                throw new InvalidOperationException($"Table '{tableName}' not found.");
            return entry.Offset + entry.Table.Schema.GetColumnOrdinal(columnName);
        }

        var matches = new List<int>();
        foreach (var (table, offset) in context)
        {
            if (table.Schema.TryGetColumnOrdinal(columnName, out var ordinal))
                matches.Add(offset + ordinal);
        }

        return matches.Count switch
        {
            0 => throw new InvalidOperationException($"Column '{columnName}' does not exist."),
            1 => matches[0],
            _ => throw new InvalidOperationException($"Column '{columnName}' is ambiguous across joined tables. Use table-qualified names.")
        };
    }

    private static bool EvaluateComparison(object? columnValue, ComparisonOp op, object literalValue)
    {
        if (columnValue is null)
            return false;

        if (columnValue is byte[] columnBytes)
        {
            if (literalValue is not byte[] literalBytes)
                throw new InvalidOperationException("Cannot compare a blob column with a non-blob value.");
            var blobEqual = columnBytes.SequenceEqual(literalBytes);
            return op switch
            {
                ComparisonOp.Equals => blobEqual,
                ComparisonOp.NotEquals => !blobEqual,
                _ => throw new InvalidOperationException("Blob columns only support = and <> comparisons.")
            };
        }

        if (columnValue is not IComparable comparable)
            throw new InvalidOperationException($"Column value of type '{columnValue.GetType().Name}' does not support comparison.");

        int cmp;
        try
        {
            cmp = comparable.CompareTo(literalValue);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"Cannot compare a value of type '{columnValue.GetType().Name}' with a literal of type '{literalValue.GetType().Name}'.");
        }

        return op switch
        {
            ComparisonOp.Equals => cmp == 0,
            ComparisonOp.NotEquals => cmp != 0,
            ComparisonOp.LessThan => cmp < 0,
            ComparisonOp.GreaterThan => cmp > 0,
            ComparisonOp.LessThanOrEquals => cmp <= 0,
            ComparisonOp.GreaterThanOrEquals => cmp >= 0,
            _ => throw new InvalidOperationException($"Unknown comparison operator {op}.")
        };
    }

    private IReadOnlyList<(TableInfo Table, int Offset)> BuildJoinContext(
        TableInfo fromTable,
        IReadOnlyList<JoinClause> joins)
    {
        var context = new List<(TableInfo Table, int Offset)>
        {
            (fromTable, 0)
        };

        var offset = fromTable.Schema.Columns.Count;
        foreach (var join in joins)
        {
            var joinTable = _storage.GetTable(join.TableName);

            if (context.Any(e => string.Equals(e.Table.TableName, joinTable.TableName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Table '{join.TableName}' appears more than once in the query. Self-joins are not supported.");
            }

            context.Add((joinTable, offset));
            offset += joinTable.Schema.Columns.Count;
        }

        return context;
    }

    private static (TableInfo Table, int Offset) FindTableEntry(
        IReadOnlyList<(TableInfo Table, int Offset)> context,
        string tableName)
    {
        var entry = context.FirstOrDefault(e =>
            string.Equals(e.Table.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        if (entry.Table is null)
            throw new InvalidOperationException($"Table '{tableName}' not found in the ON condition.");
        return entry;
    }

    private static (int[] Indices, string[] Names) BindJoinColumns(
        IReadOnlyList<ColumnReference>? columns,
        IReadOnlyList<(TableInfo Table, int Offset)> context)
    {
        if (columns is null)
        {
            var allIndices = new List<int>();
            var allNames = new List<string>();
            foreach (var (table, offset) in context)
            {
                for (var i = 0; i < table.Schema.Columns.Count; i++)
                {
                    allIndices.Add(offset + i);
                    allNames.Add($"{table.TableName}.{table.Schema.Columns[i].Name}");
                }
            }
            return (allIndices.ToArray(), allNames.ToArray());
        }

        if (columns.Count == 0)
            throw new InvalidOperationException("SELECT requires at least one projected column.");

        var indices = new int[columns.Count];
        var names = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            indices[i] = ResolveColumn(col.TableName, col.ColumnName, context);
            names[i] = col.TableName is not null
                ? $"{col.TableName}.{col.ColumnName}"
                : col.ColumnName;
        }

        return (indices, names);
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        if (left is byte[] lb && right is byte[] rb) return lb.SequenceEqual(rb);
        return left.Equals(right);
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
