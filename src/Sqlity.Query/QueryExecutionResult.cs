namespace Sqlity.Query;

public sealed class QueryExecutionResult
{
    private QueryExecutionResult(int rowsAffected, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        RowsAffected = rowsAffected;
        Columns = columns;
        Rows = rows;
    }

    public int RowsAffected { get; }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<object?[]> Rows { get; }

    public static QueryExecutionResult Empty(int rowsAffected) =>
        new(rowsAffected, Array.Empty<string>(), Array.Empty<object?[]>());

    public static QueryExecutionResult WithRows(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows) =>
        new(0, columns, rows);
}
