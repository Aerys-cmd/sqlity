namespace Sqlity.Storage.Statistics;

/// <summary>
/// Per-table statistics collected by <c>ANALYZE</c>. Used by the cost-based query planner
/// to estimate the selectivity of index seeks vs full scans.
/// </summary>
public sealed record TableStatistics(
    long RowCount,
    IReadOnlyDictionary<string, long> ColumnNdv);
