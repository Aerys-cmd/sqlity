namespace Sqlity.Storage.Rows;

public sealed record ColumnDefinition(
    string Name,
    ColumnType Type,
    bool IsNullable = true,
    bool HasDefault = false,
    object? DefaultValue = null,
    bool IsAutoIncrement = false);
