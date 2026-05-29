using Sqlity.Storage;
using Sqlity.Storage.BTree;
using Sqlity.Storage.Catalog;

namespace Sqlity.Query.Planner;

/// <summary>
/// Cost-based query planner. When table statistics are available (via <c>ANALYZE</c>),
/// selects the access path with the lowest estimated row count using a simple selectivity
/// model: <c>seek cost = rowCount × Π(1 / ndv_col_i)</c>. Falls back to the rule-based
/// leading-equality-column scoring when no statistics have been collected.
/// </summary>
internal sealed class QueryPlanner
{
    private readonly StorageEngine _storage;

    public QueryPlanner(StorageEngine storage)
    {
        _storage = storage;
    }

    public PhysicalPlan Plan(TableInfo table, WhereExpression? filter) =>
        Plan(table, filter, orderBy: null);

    /// <summary>
    /// Plans a single-table scan, optionally exploiting an index to satisfy ORDER BY
    /// without an in-memory sort.
    /// </summary>
    public PhysicalPlan Plan(TableInfo table, WhereExpression? filter, IReadOnlyList<OrderByTerm>? orderBy)
    {
        return BuildPhysical(BuildLogical(table, filter, orderBy));
    }

    private LogicalPlan BuildLogical(TableInfo table, WhereExpression? filter, IReadOnlyList<OrderByTerm>? orderBy)
    {
        var atoms = filter is not null ? FlattenAnds(filter) : [];
        var indexes = _storage.GetIndexesForTable(table.TableName);

        var stats = _storage.GetStatistics(table.TableName);

        // Cost-based selection is only useful when the table has rows; with 0 rows every
        // plan costs the same, so fall back to rule-based scoring to preserve deterministic
        // index selection and avoid penalising queries on freshly-created tables.
        var useCostModel = stats is not null && stats.RowCount > 0;

        // ── Try to satisfy WHERE via an index seek ──────────────────────────────
        IndexInfo? bestSeekIndex = null;
        double bestSeekCost = double.MaxValue;
        int bestSeekScore = 0; // used as tiebreaker / fallback when no stats
        IndexSeekRange? bestRange = null;
        List<WhereExpression>? bestPostFilter = null;

        if (filter is not null)
        {
            foreach (var index in indexes)
            {
                var (score, range, postFilter) = ScoreIndex(table, index, atoms);
                if (score == 0)
                    continue;

                if (useCostModel)
                {
                    var cost = EstimateSeekCost(stats!, index, table, atoms, score);
                    if (cost < bestSeekCost || (cost == bestSeekCost && score > bestSeekScore))
                    {
                        bestSeekCost = cost;
                        bestSeekScore = score;
                        bestSeekIndex = index;
                        bestRange = range;
                        bestPostFilter = postFilter;
                    }
                }
                else
                {
                    // No statistics — fall back to rule-based scoring.
                    if (score > bestSeekScore)
                    {
                        bestSeekScore = score;
                        bestSeekIndex = index;
                        bestRange = range;
                        bestPostFilter = postFilter;
                    }
                }
            }
        }

        // When cost model is active, only prefer the index if its estimated cost beats a full scan.
        bool useIndexSeek = bestSeekIndex is not null && bestRange is not null;
        if (useIndexSeek && useCostModel)
        {
            var fullScanCost = (double)stats!.RowCount;
            useIndexSeek = bestSeekCost < fullScanCost;
        }

        if (useIndexSeek)
        {
            var postFilterExpr = bestPostFilter is { Count: > 0 } ? CombineAnds(bestPostFilter) : null;
            return new LogicalIndexSeek(table, bestSeekIndex!, bestRange!, postFilterExpr);
        }

        // ── Try to satisfy ORDER BY via an ordered index scan ──────────────────
        if (orderBy is { Count: > 0 })
        {
            foreach (var index in indexes)
            {
                if (IndexSatisfiesOrderBy(table, index, orderBy))
                {
                    var descending = orderBy[0].Descending;
                    return new LogicalIndexOrderedScan(table, index, filter, descending);
                }
            }
        }

        return new LogicalScan(table, filter);
    }

    /// <summary>
    /// Estimates the number of rows returned by an index seek using per-column NDV statistics.
    /// For each leading equality-covered column, multiplies the selectivity factor (1/ndv).
    /// </summary>
    private static double EstimateSeekCost(
        Storage.Statistics.TableStatistics stats,
        IndexInfo index,
        TableInfo table,
        IReadOnlyList<WhereExpression> atoms,
        int coveredLeadingColumns)
    {
        var cost = (double)stats.RowCount;

        for (var i = 0; i < coveredLeadingColumns && i < index.Columns.Count; i++)
        {
            var colName = index.Columns[i];
            if (stats.ColumnNdv.TryGetValue(colName, out var ndv) && ndv > 1)
                cost /= ndv;
        }

        return cost;
    }

    /// <summary>
    /// Returns true if the index's leading columns match the ORDER BY terms (same column names,
    /// in the same sequence). All ORDER BY terms must share the same direction (all ASC or all DESC),
    /// and must not exceed the index's column count.
    /// </summary>
    private static bool IndexSatisfiesOrderBy(
        TableInfo table,
        IndexInfo index,
        IReadOnlyList<OrderByTerm> orderBy)
    {
        if (orderBy.Count > index.Columns.Count)
            return false;

        var firstDescending = orderBy[0].Descending;
        for (var i = 1; i < orderBy.Count; i++)
        {
            if (orderBy[i].Descending != firstDescending)
                return false;
        }

        for (var i = 0; i < orderBy.Count; i++)
        {
            if (!string.Equals(orderBy[i].Column.ColumnName, index.Columns[i], StringComparison.OrdinalIgnoreCase))
                return false;

            if (!table.Schema.TryGetColumnOrdinal(orderBy[i].Column.ColumnName, out _))
                return false;
        }

        return true;
    }

    private static PhysicalPlan BuildPhysical(LogicalPlan logical) => logical switch
    {
        LogicalScan scan => new PhysicalFullScan(scan.Table, scan.Filter),
        LogicalIndexSeek seek => new PhysicalIndexSeek(seek.Table, seek.Index, seek.Range, seek.PostFilter),
        LogicalIndexOrderedScan ordered => new PhysicalIndexOrderedScan(ordered.Table, ordered.Index, ordered.PostFilter, ordered.Descending),
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
