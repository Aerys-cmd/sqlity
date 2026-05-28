using Sqlity.Storage;
using Sqlity.Storage.Catalog;

namespace Sqlity.Query.Planner;

/// <summary>
/// Executes physical plans produced by <see cref="QueryPlanner"/>, returning the matching rows.
/// Also exposes the <see cref="Evaluate"/> helper used by multi-table (JOIN) execution in QueryEngine.
/// </summary>
internal sealed class QueryExecutor
{
    private readonly StorageEngine _storage;

    public QueryExecutor(StorageEngine storage)
    {
        _storage = storage;
    }

    public IReadOnlyList<object?[]> Execute(PhysicalPlan plan) => plan switch
    {
        PhysicalFullScan scan => ExecuteFullScan(scan),
        PhysicalIndexSeek seek => ExecuteIndexSeek(seek),
        PhysicalIndexOrderedScan ordered => ExecuteIndexOrderedScan(ordered),
        _ => throw new InvalidOperationException($"Unknown physical plan type '{plan.GetType().Name}'.")
    };

    private IReadOnlyList<object?[]> ExecuteFullScan(PhysicalFullScan plan)
    {
        if (plan.Filter is null)
            return _storage.ReadAll(plan.Table.TableName);

        var context = new[] { (Table: plan.Table, Offset: 0) };
        return _storage.ReadAll(plan.Table.TableName)
            .Where(row => Evaluate(plan.Filter, row, context))
            .ToList();
    }

    private IReadOnlyList<object?[]> ExecuteIndexSeek(PhysicalIndexSeek plan)
    {
        var pks = _storage.SeekByIndex(plan.Index, plan.Range);
        var context = new[] { (Table: plan.Table, Offset: 0) };
        var rows = new List<object?[]>(pks.Count);

        foreach (var pk in pks)
        {
            if (!_storage.TryReadByPrimaryKey(plan.Table.TableName, pk, out var row) || row is null)
                continue;

            if (plan.PostFilter is null || Evaluate(plan.PostFilter, row, context))
                rows.Add(row);
        }

        return rows;
    }

    private IReadOnlyList<object?[]> ExecuteIndexOrderedScan(PhysicalIndexOrderedScan plan)
    {
        var pks = _storage.ScanIndexAllOrdered(plan.Index);
        var context = new[] { (Table: plan.Table, Offset: 0) };
        var rows = new List<object?[]>(pks.Count);

        foreach (var pk in pks)
        {
            if (!_storage.TryReadByPrimaryKey(plan.Table.TableName, pk, out var row) || row is null)
                continue;

            if (plan.PostFilter is null || Evaluate(plan.PostFilter, row, context))
                rows.Add(row);
        }

        if (plan.Descending)
            rows.Reverse();

        return rows;
    }

    // ── WHERE evaluation (shared with QueryEngine for JOIN paths) ───────────────

    internal static bool Evaluate(
        WhereExpression filter,
        object?[] row,
        IReadOnlyList<(TableInfo Table, int Offset)> context) => filter switch
    {
        BinaryLogicalExpression binary => binary.Op == LogicalOp.And
            ? Evaluate(binary.Left, row, context) && Evaluate(binary.Right, row, context)
            : Evaluate(binary.Left, row, context) || Evaluate(binary.Right, row, context),

        ComparisonExpression cmp =>
            EvaluateComparison(row[ResolveColumn(cmp.TableName, cmp.ColumnName, context)], cmp.Op, cmp.Value.Value),

        NullCheckExpression nullCheck =>
            EvaluateNullCheck(row[ResolveColumn(nullCheck.TableName, nullCheck.ColumnName, context)], nullCheck.ExpectNull),

        InValuesExpression inValues =>
            EvaluateInValues(row[ResolveColumn(inValues.TableName, inValues.ColumnName, context)], inValues.Values, inValues.Negated),

        LikeExpression like =>
            EvaluateLike(row[ResolveColumn(like.TableName, like.ColumnName, context)], like.Pattern, like.CaseInsensitive, like.Negated),

        BetweenExpression between =>
            EvaluateBetween(row[ResolveColumn(between.TableName, between.ColumnName, context)], between.Low.Value, between.High.Value, between.Negated),

        CaseWhenWhereExpression caseWhen =>
            EvaluateCaseWhenWhere(caseWhen, row, context),

        _ => throw new InvalidOperationException($"Unknown WHERE expression type '{filter.GetType().Name}'.")
    };

    internal static bool EvaluateNullCheck(object? columnValue, bool expectNull) =>
        expectNull ? columnValue is null : columnValue is not null;

    internal static bool EvaluateInValues(object? columnValue, IReadOnlyList<object?> values, bool negated = false)
    {
        if (columnValue is null)
            return false;

        bool hasNull = false;
        foreach (var v in values)
        {
            if (v is null) { hasNull = true; continue; }

            try
            {
                if (EvaluateComparison(columnValue, ComparisonOp.Equals, v))
                    return !negated;
            }
            catch (InvalidOperationException)
            {
                // Type mismatch — treat as non-equal and continue.
            }
        }

        // No match found. For NOT IN: if there's any NULL in the list, SQL says UNKNOWN → return false.
        if (negated && hasNull)
            return false;

        return negated;
    }

    internal static int ResolveColumn(
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

    internal static bool EvaluateComparison(object? columnValue, ComparisonOp op, object? literalValue)
    {
        if (columnValue is null || literalValue is null)
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

        // Coerce literal to match column type for cross-type numerics and date strings.
        literalValue = CoerceLiteralToColumnType(columnValue, literalValue);

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

    private static object CoerceLiteralToColumnType(object columnValue, object literalValue)
    {
        if (columnValue is double && literalValue is long longToDouble)
            return (double)longToDouble;
        if (columnValue is long && literalValue is double doubleToLong)
            return (long)doubleToLong;
        if (columnValue is DateOnly && literalValue is string dateString)
            return DateOnly.Parse(dateString, System.Globalization.CultureInfo.InvariantCulture);
        if (columnValue is DateTime && literalValue is string dtString)
            return DateTime.Parse(dtString, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        return literalValue;
    }

    internal static bool EvaluateLike(object? columnValue, string pattern, bool caseInsensitive, bool negated)
    {
        if (columnValue is null)
            return false;

        var text = columnValue.ToString() ?? string.Empty;
        var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var result = MatchesLikeCore(text, 0, pattern, 0, cmp);
        return negated ? !result : result;
    }

    private static bool MatchesLikeCore(string value, int vi, string pattern, int pi, StringComparison cmp)
    {
        while (true)
        {
            if (pi == pattern.Length)
                return vi == value.Length;

            char pc = pattern[pi];
            if (pc == '%')
            {
                pi++;
                for (int i = vi; i <= value.Length; i++)
                    if (MatchesLikeCore(value, i, pattern, pi, cmp))
                        return true;
                return false;
            }
            else if (pc == '_')
            {
                if (vi >= value.Length) return false;
                vi++;
                pi++;
            }
            else
            {
                if (vi >= value.Length) return false;
                if (string.Compare(value, vi, pattern, pi, 1, cmp) != 0) return false;
                vi++;
                pi++;
            }
        }
    }

    internal static bool EvaluateBetween(object? columnValue, object? low, object? high, bool negated)
    {
        if (columnValue is null || low is null || high is null)
            return false;

        bool result;
        try
        {
            result = EvaluateComparison(columnValue, ComparisonOp.GreaterThanOrEquals, low)
                  && EvaluateComparison(columnValue, ComparisonOp.LessThanOrEquals, high);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return negated ? !result : result;
    }

    private static bool EvaluateCaseWhenWhere(
        CaseWhenWhereExpression expr,
        object?[] row,
        IReadOnlyList<(TableInfo Table, int Offset)> context)
    {
        object? result = null;
        var matched = false;

        foreach (var branch in expr.Branches)
        {
            if (Evaluate(branch.Condition, row, context))
            {
                result = ResolveScalarExpr(branch.Result, row, context);
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            if (expr.ElseResult is null)
                return false; // NULL never satisfies a comparison

            result = ResolveScalarExpr(expr.ElseResult, row, context);
        }

        if (result is null)
            return false;

        return EvaluateComparison(result, expr.Op, expr.Value.Value);
    }

    internal static object? ResolveScalarExpr(
        ScalarExpr expr,
        object?[] row,
        IReadOnlyList<(TableInfo Table, int Offset)> context) => expr switch
    {
        LiteralScalarExpr lit => lit.Value.Value,
        ColumnScalarExpr col => row[ResolveColumn(col.Column.TableName, col.Column.ColumnName, context)],
        _ => throw new InvalidOperationException($"Unknown scalar expression type '{expr.GetType().Name}'.")
    };
}
