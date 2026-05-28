namespace Sqlity.Storage.Catalog;

public sealed record ViewInfo(long ViewId, string ViewName, string SelectSql);
