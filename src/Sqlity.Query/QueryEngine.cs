using Sqlity.Query.Planner;
using Sqlity.Storage;
using Sqlity.Storage.Catalog;
using Sqlity.Storage.Rows;

namespace Sqlity.Query;

public sealed class QueryEngine : IDisposable
{
    private readonly StorageEngine _storage;
    private readonly bool _ownsStorage;
    private readonly QueryPlanner _planner;
    private readonly QueryExecutor _executor;

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
        _planner = new QueryPlanner(storage);
        _executor = new QueryExecutor(storage);
    }

    public bool InTransaction => _storage.InTransaction;

    public void BeginTransaction()
    {
        if (_storage.InTransaction)
            throw new InvalidOperationException("A transaction is already active. Nested transactions are not supported.");

        _storage.BeginTransaction();
    }

    public void Commit()
    {
        if (!_storage.InTransaction)
            throw new InvalidOperationException("No active transaction to commit.");

        _storage.Commit();
    }

    public void Rollback()
    {
        if (!_storage.InTransaction)
            throw new InvalidOperationException("No active transaction to roll back.");

        _storage.Rollback();
    }

    public QueryExecutionResult Execute(string sql)
    {
        var statements = new SqlParser(sql).ParseAll();

        if (statements.Count == 0)
            return QueryExecutionResult.Empty(rowsAffected: 0);

        QueryExecutionResult last = QueryExecutionResult.Empty(rowsAffected: 0);
        foreach (var statement in statements)
        {
            last = statement switch
            {
                BeginStatement => ExecuteBegin(),
                CommitStatement => ExecuteCommit(),
                RollbackStatement => ExecuteRollback(),
                _ => ExecuteDml(statement)
            };
        }
        return last;
    }

    public void Dispose()
    {
        if (_ownsStorage)
        {
            // Roll back any uncommitted transaction so the database file is left consistent.
            if (_storage.InTransaction)
                _storage.Rollback();

            _storage.Dispose();
        }
    }

    // ── transaction statement execution ─────────────────────────────────────

    private QueryExecutionResult ExecuteBegin()
    {
        BeginTransaction();
        return QueryExecutionResult.Empty(rowsAffected: 0);
    }

    private QueryExecutionResult ExecuteCommit()
    {
        Commit();
        return QueryExecutionResult.Empty(rowsAffected: 0);
    }

    private QueryExecutionResult ExecuteRollback()
    {
        Rollback();
        return QueryExecutionResult.Empty(rowsAffected: 0);
    }

    // ── DML / DDL with auto-commit ───────────────────────────────────────────

    private QueryExecutionResult ExecuteDml(SqlStatement statement)
    {
        var autoCommit = !_storage.InTransaction;
        if (autoCommit)
            _storage.BeginTransaction();

        try
        {
            var result = statement switch
            {
                CreateTableStatement createTable => ExecuteCreateTable(createTable),
                CreateIndexStatement createIndex => ExecuteCreateIndex(createIndex),
                InsertStatement insert => ExecuteInsert(insert),
                SelectStatement select => ExecuteSelect(select),
                DeleteStatement delete => ExecuteDelete(delete),
                UpdateStatement update => ExecuteUpdate(update),
                DropTableStatement dropTable => ExecuteDropTable(dropTable),
                AlterTableRenameStatement alterRename => ExecuteAlterTableRename(alterRename),
                AlterTableAddColumnStatement alterAdd => ExecuteAlterTableAddColumn(alterAdd),
                AlterTableRenameColumnStatement alterRenameCol => ExecuteAlterTableRenameColumn(alterRenameCol),
                _ => throw new InvalidOperationException($"Unsupported statement type {statement.GetType().Name}.")
            };

            if (autoCommit)
                _storage.Commit();

            return result;
        }
        catch
        {
            if (autoCommit)
                _storage.Rollback();

            throw;
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
            var isPrimaryKey = column.IsPrimaryKey;
            var isNullable = !column.IsNotNull && !isPrimaryKey;
            columns[index] = new ColumnDefinition(column.Name, ResolveColumnType(column.TypeName), isNullable);

            if (isPrimaryKey)
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

    private QueryExecutionResult ExecuteCreateIndex(CreateIndexStatement statement)
    {
        _storage.CreateIndex(statement.IndexName, statement.TableName, statement.Columns, statement.IsUnique);
        return QueryExecutionResult.Empty(rowsAffected: 0);
    }

    private QueryExecutionResult ExecuteDropTable(DropTableStatement statement)
    {
        _storage.DropTable(statement.TableName);
        return QueryExecutionResult.Empty(rowsAffected: 0);
    }

    private QueryExecutionResult ExecuteAlterTableRename(AlterTableRenameStatement statement)
    {
        _storage.RenameTable(statement.OldName, statement.NewName);
        return QueryExecutionResult.Empty(rowsAffected: 0);
    }

    private QueryExecutionResult ExecuteAlterTableAddColumn(AlterTableAddColumnStatement statement)
    {
        var col = statement.Column;
        var isNullable = !col.IsNotNull && !col.IsPrimaryKey;
        _storage.AddColumn(statement.TableName, new ColumnDefinition(col.Name, ResolveColumnType(col.TypeName), isNullable));
        return QueryExecutionResult.Empty(rowsAffected: 0);
    }

    private QueryExecutionResult ExecuteAlterTableRenameColumn(AlterTableRenameColumnStatement statement)
    {
        _storage.RenameColumn(statement.TableName, statement.OldColumnName, statement.NewColumnName);
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

        // Aggregate queries are handled via a separate path.
        var hasAggregates = statement.Columns is not null &&
                            statement.Columns.Any(c => c is AggregateSelectItem);
        if (hasAggregates || statement.GroupBy is { Count: > 0 })
            return ExecuteSelectWithAggregation(fromTable, statement);

        if (statement.Joins.Count == 0)
        {
            // Route through the query planner (now ORDER BY-aware).
            var plan = _planner.Plan(fromTable, statement.Filter, statement.OrderBy);
            var rows = _executor.Execute(plan);

            // Determine whether the plan already delivered ordered rows.
            var alreadyOrdered = plan is PhysicalIndexOrderedScan;

            IEnumerable<object?[]> rowSeq = rows;
            if (statement.OrderBy is { Count: > 0 } && !alreadyOrdered)
            {
                rowSeq = ApplyOrderBy(rows, statement.OrderBy,
                    new[] { (Table: fromTable, Offset: 0) });
            }

            rowSeq = ApplyLimitOffset(rowSeq, statement.Limit, statement.Offset);

            var (selectedOrdinals, projectedColumns, projectedColumnTypes) =
                BindSingleTableSelectItems(fromTable, statement.Columns);

            var projectedRows = rowSeq
                .Select(row => selectedOrdinals.Select(i => row[i]).ToArray())
                .ToArray();

            return QueryExecutionResult.WithRows(projectedColumns, projectedRows, projectedColumnTypes);
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
            filteredRows = currentRows.Where(row => QueryExecutor.Evaluate(statement.Filter, row, context));
        }

        if (statement.OrderBy is { Count: > 0 })
            filteredRows = ApplyOrderBy(filteredRows, statement.OrderBy, context);

        filteredRows = ApplyLimitOffset(filteredRows, statement.Limit, statement.Offset);

        var (selectedIndices, outputColumnNames, outputColumnTypes) = BindJoinColumns(statement.Columns, context);

        var projectedRows2 = filteredRows
            .Select(row => selectedIndices.Select(i => row[i]).ToArray())
            .ToArray();

        return QueryExecutionResult.WithRows(outputColumnNames, projectedRows2, outputColumnTypes);
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
            .Where(row => QueryExecutor.Evaluate(statement.Filter, row, context))
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
            .Where(row => QueryExecutor.Evaluate(statement.Filter, row, context))
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

        for (var index = 0; index < assignedColumns.Length; index++)
        {
            if (!assignedColumns[index])
            {
                if (!table.Schema.Columns[index].IsNullable)
                {
                    throw new InvalidOperationException(
                        $"Column '{table.Schema.Columns[index].Name}' is NOT NULL and must be provided in the INSERT statement.");
                }

                boundValues[index] = null;
            }
        }

        return boundValues;
    }

    /// <summary>
    /// Resolves the column ordinals, names, and types for a single-table SELECT.
    /// Handles both plain column references and aggregate items (aggregate columns are NOT included here;
    /// they are handled separately by the aggregation path).
    /// </summary>
    private static (int[] Ordinals, string[] Names, ColumnType[] Types) BindSingleTableSelectItems(
        TableInfo table,
        IReadOnlyList<SelectItem>? items)
    {
        if (items is null)
        {
            var allOrdinals = Enumerable.Range(0, table.Schema.Columns.Count).ToArray();
            var allNames = allOrdinals.Select(i => table.Schema.Columns[i].Name).ToArray();
            var allTypes = allOrdinals.Select(i => table.Schema.Columns[i].Type).ToArray();
            return (allOrdinals, allNames, allTypes);
        }

        if (items.Count == 0)
            throw new InvalidOperationException("SELECT requires at least one projected column.");

        var ordinals = new List<int>();
        var names = new List<string>();
        var types = new List<ColumnType>();

        foreach (var item in items)
        {
            if (item is not ColumnSelectItem colItem)
                throw new InvalidOperationException("Aggregate functions are not allowed outside a GROUP BY context.");

            var col = colItem.Column;
            if (col.TableName is not null &&
                !string.Equals(col.TableName, table.TableName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Table '{col.TableName}' not found in the FROM clause.");
            }

            var ordinal = table.Schema.GetColumnOrdinal(col.ColumnName);
            ordinals.Add(ordinal);
            names.Add(col.TableName is not null ? $"{col.TableName}.{col.ColumnName}" : col.ColumnName);
            types.Add(table.Schema.Columns[ordinal].Type);
        }

        return (ordinals.ToArray(), names.ToArray(), types.ToArray());
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
            .Where(row => QueryExecutor.Evaluate(filter, row, context))
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

    private static (int[] Indices, string[] Names, ColumnType[] Types) BindJoinColumns(
        IReadOnlyList<SelectItem>? items,
        IReadOnlyList<(TableInfo Table, int Offset)> context)
    {
        var flatTypes = BuildFlatTypeArray(context);

        if (items is null)
        {
            var allIndices = new List<int>();
            var allNames = new List<string>();
            var allTypes = new List<ColumnType>();
            foreach (var (table, offset) in context)
            {
                for (var i = 0; i < table.Schema.Columns.Count; i++)
                {
                    allIndices.Add(offset + i);
                    allNames.Add($"{table.TableName}.{table.Schema.Columns[i].Name}");
                    allTypes.Add(table.Schema.Columns[i].Type);
                }
            }
            return (allIndices.ToArray(), allNames.ToArray(), allTypes.ToArray());
        }

        if (items.Count == 0)
            throw new InvalidOperationException("SELECT requires at least one projected column.");

        var indices = new List<int>();
        var names = new List<string>();
        var types = new List<ColumnType>();

        foreach (var item in items)
        {
            if (item is not ColumnSelectItem colItem)
                throw new InvalidOperationException("Aggregate functions are not allowed in JOIN queries.");

            var col = colItem.Column;
            var idx = QueryExecutor.ResolveColumn(col.TableName, col.ColumnName, context);
            indices.Add(idx);
            names.Add(col.TableName is not null ? $"{col.TableName}.{col.ColumnName}" : col.ColumnName);
            types.Add(flatTypes[idx]);
        }

        return (indices.ToArray(), names.ToArray(), types.ToArray());
    }

    private static ColumnType[] BuildFlatTypeArray(IReadOnlyList<(TableInfo Table, int Offset)> context)
    {
        var totalCols = context.Sum(e => e.Table.Schema.Columns.Count);
        var flatTypes = new ColumnType[totalCols];
        foreach (var (table, offset) in context)
            for (var i = 0; i < table.Schema.Columns.Count; i++)
                flatTypes[offset + i] = table.Schema.Columns[i].Type;
        return flatTypes;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        if (left is byte[] lb && right is byte[] rb) return lb.SequenceEqual(rb);
        return left.Equals(right);
    }

    private static object? ConvertLiteral(ColumnDefinition column, SqlLiteral literal)
    {
        if (literal.Value is null)
        {
            if (!column.IsNullable)
            {
                throw new InvalidOperationException($"Cannot assign NULL to non-nullable column '{column.Name}'.");
            }

            return null;
        }

        return column.Type switch
        {
            ColumnType.Int64 when literal.Value is long longValue => longValue,
            ColumnType.String when literal.Value is string stringValue => stringValue,
            ColumnType.Blob when literal.Value is byte[] blobValue => blobValue,
            ColumnType.Boolean when literal.Value is bool boolValue => boolValue,
            _ => throw new InvalidOperationException($"Value '{literal.Value}' is not valid for column '{column.Name}' of type {column.Type}.")
        };
    }

    private static ColumnType ResolveColumnType(string typeName) =>
        typeName.ToUpperInvariant() switch
        {
            "INT64" or "INTEGER" or "BIGINT" => ColumnType.Int64,
            "STRING" or "TEXT" => ColumnType.String,
            "BLOB" => ColumnType.Blob,
            "BOOLEAN" or "BOOL" => ColumnType.Boolean,
            _ => throw new InvalidOperationException($"Unsupported SQL column type '{typeName}'.")
        };

    // ── ORDER BY / LIMIT / OFFSET ─────────────────────────────────────────────

    private static IEnumerable<object?[]> ApplyOrderBy(
        IEnumerable<object?[]> rows,
        IReadOnlyList<OrderByTerm> orderBy,
        IReadOnlyList<(TableInfo Table, int Offset)> context)
    {
        IOrderedEnumerable<object?[]>? ordered = null;

        for (var i = 0; i < orderBy.Count; i++)
        {
            var term = orderBy[i];
            var colIndex = QueryExecutor.ResolveColumn(term.Column.TableName, term.Column.ColumnName, context);

            if (i == 0)
            {
                ordered = term.Descending
                    ? rows.OrderByDescending(row => row[colIndex], NullableComparer.Instance)
                    : rows.OrderBy(row => row[colIndex], NullableComparer.Instance);
            }
            else
            {
                ordered = term.Descending
                    ? ordered!.ThenByDescending(row => row[colIndex], NullableComparer.Instance)
                    : ordered!.ThenBy(row => row[colIndex], NullableComparer.Instance);
            }
        }

        return ordered ?? rows;
    }

    private static IEnumerable<object?[]> ApplyLimitOffset(
        IEnumerable<object?[]> rows,
        int? limit,
        int? offset)
    {
        if (offset is > 0)
            rows = rows.Skip(offset.Value);
        if (limit.HasValue)
            rows = rows.Take(limit.Value);
        return rows;
    }

    // ── Aggregation ───────────────────────────────────────────────────────────

    private QueryExecutionResult ExecuteSelectWithAggregation(TableInfo table, SelectStatement statement)
    {
        // Validate: strict GROUP BY — every non-aggregate SELECT column must appear in GROUP BY.
        var groupBySet = new HashSet<string>(
            statement.GroupBy ?? [],
            StringComparer.OrdinalIgnoreCase);

        if (statement.Columns is not null)
        {
            foreach (var item in statement.Columns)
            {
                if (item is ColumnSelectItem colItem)
                {
                    if (!groupBySet.Contains(colItem.Column.ColumnName))
                        throw new InvalidOperationException(
                            $"Column '{colItem.Column.ColumnName}' must appear in GROUP BY or be used in an aggregate function.");
                }
            }
        }

        // Fetch all rows (full scan or index seek via planner, no ORDER BY on this path for now).
        var plan = _planner.Plan(table, statement.Filter);
        var allRows = _executor.Execute(plan);

        // Determine GROUP BY column ordinals.
        var groupByOrdinals = (statement.GroupBy ?? [])
            .Select(col => table.Schema.GetColumnOrdinal(col))
            .ToArray();

        // Group rows by GROUP BY key (tuple of values at group-by ordinals).
        var groups = new Dictionary<GroupKey, List<object?[]>>();
        foreach (var row in allRows)
        {
            var key = new GroupKey(groupByOrdinals.Select(i => row[i]).ToArray());
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }
            group.Add(row);
        }

        // If no rows and no GROUP BY, still produce one aggregate row (e.g. COUNT(*) → 0).
        if (groups.Count == 0 && groupByOrdinals.Length == 0)
            groups[new GroupKey([])] = [];

        // Build output column metadata.
        var outNames = new List<string>();
        var outTypes = new List<ColumnType>();

        IReadOnlyList<SelectItem> selectItems = statement.Columns
            ?? table.Schema.Columns
                .Select((col, i) => (SelectItem)new ColumnSelectItem(new ColumnReference(null, col.Name)))
                .ToList();

        foreach (var item in selectItems)
        {
            switch (item)
            {
                case ColumnSelectItem colItem:
                    outNames.Add(colItem.Column.ColumnName);
                    outTypes.Add(table.Schema.Columns[table.Schema.GetColumnOrdinal(colItem.Column.ColumnName)].Type);
                    break;
                case AggregateSelectItem aggItem:
                    outNames.Add(FormatAggregateName(aggItem));
                    outTypes.Add(aggItem.Fn == AggregateFn.Avg ? ColumnType.Float64 : ColumnType.Int64);
                    break;
            }
        }

        // Compute result rows per group.
        var resultRows = new List<object?[]>();
        foreach (var (key, groupRows) in groups)
        {
            // Apply HAVING filter.
            if (statement.Having is not null && !EvaluateHaving(statement.Having, groupRows, table))
                continue;

            var outRow = new object?[selectItems.Count];
            var keyIndex = 0;

            for (var i = 0; i < selectItems.Count; i++)
            {
                switch (selectItems[i])
                {
                    case ColumnSelectItem:
                        outRow[i] = key.Values[keyIndex++];
                        break;
                    case AggregateSelectItem aggItem:
                        outRow[i] = ComputeAggregate(aggItem, groupRows, table);
                        break;
                }
            }

            resultRows.Add(outRow);
        }

        // Apply ORDER BY and LIMIT/OFFSET after aggregation.
        IEnumerable<object?[]> resultSeq = resultRows;
        if (statement.OrderBy is { Count: > 0 })
        {
            // For aggregation results, resolve column names against output column names.
            resultSeq = ApplyOrderByOnResult(resultSeq, statement.OrderBy, outNames);
        }
        resultSeq = ApplyLimitOffset(resultSeq, statement.Limit, statement.Offset);

        return QueryExecutionResult.WithRows(outNames, resultSeq.ToArray(), outTypes);
    }

    private static string FormatAggregateName(AggregateSelectItem item) =>
        item.Argument is null
            ? $"{item.Fn}(*)"
            : $"{item.Fn}({item.Argument.ColumnName})";

    private static object? ComputeAggregate(
        AggregateSelectItem item,
        IReadOnlyList<object?[]> rows,
        TableInfo table)
    {
        if (item.Fn == AggregateFn.Count)
        {
            if (item.Argument is null)
                return (long)rows.Count;

            // COUNT(col) — count non-NULL values.
            var ordinal = table.Schema.GetColumnOrdinal(item.Argument.ColumnName);
            return (long)rows.Count(r => r[ordinal] is not null);
        }

        if (rows.Count == 0)
            return null;

        var colOrdinal = table.Schema.GetColumnOrdinal(item.Argument!.ColumnName);
        var nonNullValues = rows
            .Select(r => r[colOrdinal])
            .Where(v => v is not null)
            .ToList();

        if (nonNullValues.Count == 0)
            return null;

        return item.Fn switch
        {
            AggregateFn.Sum => nonNullValues.Aggregate(0L, (acc, v) => acc + Convert.ToInt64(v)),
            AggregateFn.Min => nonNullValues.Cast<IComparable>().Min(),
            AggregateFn.Max => nonNullValues.Cast<IComparable>().Max(),
            AggregateFn.Avg => nonNullValues.Average(v => Convert.ToDouble(v)),
            _ => throw new InvalidOperationException($"Unknown aggregate function {item.Fn}.")
        };
    }

    private static bool EvaluateHaving(
        HavingExpression having,
        IReadOnlyList<object?[]> groupRows,
        TableInfo table)
    {
        var aggItem = new AggregateSelectItem(having.Fn, having.Argument);
        var computedValue = ComputeAggregate(aggItem, groupRows, table);
        return QueryExecutor.EvaluateComparison(computedValue, having.Op, having.Value.Value);
    }

    private static IEnumerable<object?[]> ApplyOrderByOnResult(
        IEnumerable<object?[]> rows,
        IReadOnlyList<OrderByTerm> orderBy,
        IReadOnlyList<string> columnNames)
    {
        IOrderedEnumerable<object?[]>? ordered = null;

        for (var i = 0; i < orderBy.Count; i++)
        {
            var term = orderBy[i];
            var colIdx = FindColumnIndex(columnNames, term.Column.ColumnName);
            if (colIdx < 0)
                throw new InvalidOperationException($"ORDER BY column '{term.Column.ColumnName}' not found in SELECT list.");

            var idx = colIdx;
            if (i == 0)
            {
                ordered = term.Descending
                    ? rows.OrderByDescending(row => row[idx], NullableComparer.Instance)
                    : rows.OrderBy(row => row[idx], NullableComparer.Instance);
            }
            else
            {
                ordered = term.Descending
                    ? ordered!.ThenByDescending(row => row[idx], NullableComparer.Instance)
                    : ordered!.ThenBy(row => row[idx], NullableComparer.Instance);
            }
        }

        return ordered ?? rows;
    }

    private static int FindColumnIndex(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}

/// <summary>Compares values that may be null, placing nulls last.</summary>
file sealed class NullableComparer : IComparer<object?>
{
    public static readonly NullableComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return 1;  // nulls last
        if (y is null) return -1;
        if (x is IComparable cx)
        {
            try { return cx.CompareTo(y); }
            catch (ArgumentException) { return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal); }
        }
        return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>Equality key for GROUP BY bucketing.</summary>
file sealed class GroupKey : IEquatable<GroupKey>
{
    public object?[] Values { get; }

    public GroupKey(object?[] values) => Values = values;

    public bool Equals(GroupKey? other)
    {
        if (other is null) return false;
        if (Values.Length != other.Values.Length) return false;
        for (var i = 0; i < Values.Length; i++)
        {
            var a = Values[i]; var b = other.Values[i];
            if (a is null && b is null) continue;
            if (a is null || b is null) return false;
            if (a is byte[] ab && b is byte[] bb) { if (!ab.SequenceEqual(bb)) return false; continue; }
            if (!a.Equals(b)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is GroupKey gk && Equals(gk);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        foreach (var v in Values)
        {
            if (v is byte[] bytes) foreach (var b in bytes) hc.Add(b);
            else hc.Add(v);
        }
        return hc.ToHashCode();
    }
}
