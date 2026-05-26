using Sqlity.Storage.Rows;

namespace Sqlity.Query;

public sealed class QueryExecutionResult
{
    private QueryExecutionResult(
        int rowsAffected,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        IReadOnlyList<ColumnType> columnTypes)
    {
        if (columnTypes.Count > 0 && columnTypes.Count != columns.Count)
            throw new ArgumentException("columnTypes.Count must equal columns.Count.");

        RowsAffected = rowsAffected;
        Columns = columns;
        Rows = rows;
        ColumnTypes = columnTypes;
    }

    public int RowsAffected { get; }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<object?[]> Rows { get; }

    /// <summary>
    /// Schema-declared column types, parallel to <see cref="Columns"/>.
    /// Empty when no schema metadata is available (e.g. for non-SELECT results).
    /// </summary>
    public IReadOnlyList<ColumnType> ColumnTypes { get; }

    public static QueryExecutionResult Empty(int rowsAffected) =>
        new(rowsAffected, Array.Empty<string>(), Array.Empty<object?[]>(), Array.Empty<ColumnType>());

    public static QueryExecutionResult WithRows(
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        IReadOnlyList<ColumnType> columnTypes) =>
        new(0, columns, rows, columnTypes);

    // Backward-compatible overload — no schema type info; GetFieldType falls back to runtime sniffing.
    public static QueryExecutionResult WithRows(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows) =>
        new(0, columns, rows, Array.Empty<ColumnType>());
}
