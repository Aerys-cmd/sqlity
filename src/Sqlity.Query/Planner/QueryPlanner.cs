using Sqlity.Storage;
using Sqlity.Storage.BTree;
using Sqlity.Storage.Catalog;

namespace Sqlity.Query.Planner;

/// <summary>
/// Rule-based query planner. Scores candidate indexes by leading equality column coverage
/// and selects the best index seek, falling back to a full scan when no index helps.
/// </summary>
internal sealed class QueryPlanner
{
    private readonly StorageEngine _storage;

    public QueryPlanner(StorageEngine storage)
    {
        _storage = storage;
    }

    public PhysicalPlan Plan(TableInfo table, WhereExpression? filter)
    {
        return BuildPhysical(BuildLogical(table, filter));
    }

    private LogicalPlan BuildLogical(TableInfo table, WhereExpression? filter)
    {
        if (filter is null)
            return new LogicalScan(table, null);

        var atoms = FlattenAnds(filter);
        var indexes = _storage.GetIndexesForTable(table.TableName);

        IndexInfo? bestIndex = null;
        int bestScore = 0;
        IndexSeekRange? bestRange = null;
        List<WhereExpression>? bestPostFilter = null;

        foreach (var index in indexes)
        {
            var (score, range, postFilter) = ScoreIndex(table, index, atoms);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
                bestRange = range;
                bestPostFilter = postFilter;
            }
        }

        if (bestIndex is null || bestRange is null)
            return new LogicalScan(table, filter);

        var postFilterExpr = bestPostFilter is { Count: > 0 }
            ? CombineAnds(bestPostFilter)
            : null;

        return new LogicalIndexSeek(table, bestIndex, bestRange, postFilterExpr);
    }

    private static PhysicalPlan BuildPhysical(LogicalPlan logical) => logical switch
    {
        LogicalScan scan => new PhysicalFullScan(scan.Table, scan.Filter),
        LogicalIndexSeek seek => new PhysicalIndexSeek(seek.Table, seek.Index, seek.Range, seek.PostFilter),
        _ => throw new InvalidOperationException($"Unknown logical plan type '{logical.GetType().Name}'.")
    };

    // ── Predicate analysis ───────────────────────────────────────────────────────

    /// <summary>
    /// Scores an index for a given set of predicate atoms. Returns the count of
    /// leading index columns covered by equality predicates, the resulting seek range,
    /// and any unmatched predicates that become a post-filter.
    /// </summary>
    private static (int Score, IndexSeekRange? Range, List<WhereExpression> PostFilter) ScoreIndex(
        TableInfo table,
        IndexInfo index,
        IReadOnlyList<WhereExpression> atoms)
    {
        var columnOrdinals = index.Columns
            .Select(c => table.Schema.GetColumnOrdinal(c))
            .ToList();

        // Collect equality predicates by column ordinal.
        var equalityByOrdinal = new Dictionary<int, object?>();
        foreach (var atom in atoms)
        {
            if (atom is ComparisonExpression { Op: ComparisonOp.Equals } cmp &&
                IsPredicateForTable(cmp.TableName, table.TableName) &&
                table.Schema.TryGetColumnOrdinal(cmp.ColumnName, out var ord))
            {
                equalityByOrdinal.TryAdd(ord, cmp.Value.Value);
            }
        }

        // Count contiguous leading columns covered by equality.
        int score = 0;
        var matchedOrdinals = new List<int>(columnOrdinals.Count);
        var matchedValues = new List<object?>(columnOrdinals.Count);

        for (int i = 0; i < columnOrdinals.Count; i++)
        {
            if (!equalityByOrdinal.TryGetValue(columnOrdinals[i], out var val))
                break;

            score++;
            matchedOrdinals.Add(columnOrdinals[i]);
            matchedValues.Add(val);
        }

        if (score == 0)
            return (0, null, new List<WhereExpression>(atoms));

        // Build a fake full-row array with matched values at their ordinal positions.
        var fakeRow = new object?[table.Schema.Columns.Count];
        for (int i = 0; i < matchedOrdinals.Count; i++)
            fakeRow[matchedOrdinals[i]] = matchedValues[i];

        var prefixKey = IndexKeyEncoder.Encode(
            table.Schema.Columns,
            matchedOrdinals,
            fakeRow);

        IndexSeekRange range = (index.IsUnique && score == index.Columns.Count)
            ? IndexSeekRange.Equality(prefixKey)
            : IndexSeekRange.PrefixEquality(prefixKey);

        var matchedSet = new HashSet<int>(matchedOrdinals);
        var postFilter = atoms
            .Where(a => !IsEqualityPredicateCoveredBy(a, table, matchedSet))
            .ToList();

        return (score, range, postFilter);
    }

    private static bool IsPredicateForTable(string? tableName, string tableName2) =>
        tableName is null || string.Equals(tableName, tableName2, StringComparison.OrdinalIgnoreCase);

    private static bool IsEqualityPredicateCoveredBy(
        WhereExpression atom,
        TableInfo table,
        HashSet<int> coveredOrdinals)
    {
        return atom is ComparisonExpression { Op: ComparisonOp.Equals } c &&
               IsPredicateForTable(c.TableName, table.TableName) &&
               table.Schema.TryGetColumnOrdinal(c.ColumnName, out var o) &&
               coveredOrdinals.Contains(o);
    }

    // ── AND-flattening helpers ───────────────────────────────────────────────────

    private static IReadOnlyList<WhereExpression> FlattenAnds(WhereExpression expr)
    {
        var atoms = new List<WhereExpression>();
        FlattenAndsCore(expr, atoms);
        return atoms;
    }

    private static void FlattenAndsCore(WhereExpression expr, List<WhereExpression> atoms)
    {
        if (expr is BinaryLogicalExpression { Op: LogicalOp.And } and)
        {
            FlattenAndsCore(and.Left, atoms);
            FlattenAndsCore(and.Right, atoms);
        }
        else
        {
            atoms.Add(expr);
        }
    }

    private static WhereExpression CombineAnds(IReadOnlyList<WhereExpression> atoms)
    {
        var result = atoms[0];
        for (int i = 1; i < atoms.Count; i++)
            result = new BinaryLogicalExpression(result, LogicalOp.And, atoms[i]);
        return result;
    }
}
