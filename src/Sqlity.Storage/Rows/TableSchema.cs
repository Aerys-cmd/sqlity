namespace Sqlity.Storage.Rows;

public sealed class TableSchema
{
    private readonly Dictionary<string, int> _columnOrdinals;

    public TableSchema(string tableName, IReadOnlyList<ColumnDefinition> columns, int primaryKeyOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException("A table must define at least one column.", nameof(columns));
        }

        if (primaryKeyOrdinal < 0 || primaryKeyOrdinal >= columns.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(primaryKeyOrdinal), "The primary key ordinal must point to an existing column.");
        }

        if (columns[primaryKeyOrdinal].Type != ColumnType.Int64)
        {
            throw new NotSupportedException("The initial storage engine only supports Int64 primary keys.");
        }

        // Primary keys are always non-nullable; coerce silently so callers that omit
        // IsNullable (which defaults to true) aren't penalised.
        var cols = columns.ToArray();
        if (cols[primaryKeyOrdinal].IsNullable)
        {
            cols[primaryKeyOrdinal] = cols[primaryKeyOrdinal] with { IsNullable = false };
        }

        TableName = tableName;
        Columns = cols;
        PrimaryKeyOrdinal = primaryKeyOrdinal;
        _columnOrdinals = BuildColumnOrdinals(Columns);
    }

    public string TableName { get; }

    public IReadOnlyList<ColumnDefinition> Columns { get; }

    public int PrimaryKeyOrdinal { get; }

    public ColumnDefinition PrimaryKeyColumn => Columns[PrimaryKeyOrdinal];

    public bool TryGetColumnOrdinal(string columnName, out int ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        return _columnOrdinals.TryGetValue(columnName, out ordinal);
    }

    public int GetColumnOrdinal(string columnName)
    {
        if (!TryGetColumnOrdinal(columnName, out var ordinal))
        {
            throw new InvalidOperationException($"Column '{columnName}' does not exist in table '{TableName}'.");
        }

        return ordinal;
    }

    private static Dictionary<string, int> BuildColumnOrdinals(IReadOnlyList<ColumnDefinition> columns)
    {
        var ordinals = new Dictionary<string, int>(columns.Count, StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            if (string.IsNullOrWhiteSpace(column.Name))
            {
                throw new ArgumentException("Column names cannot be null or whitespace.", nameof(columns));
            }

            if (!ordinals.TryAdd(column.Name, index))
            {
                throw new ArgumentException($"Column '{column.Name}' is defined more than once.", nameof(columns));
            }
        }

        return ordinals;
    }
}
