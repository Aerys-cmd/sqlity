namespace Sqlity.Storage.Catalog;

public sealed record IndexInfo(
    long IndexId,
    string IndexName,
    string TableName,
    uint RootPageId,
    IReadOnlyList<string> Columns,
    bool IsUnique);
