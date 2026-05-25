using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Catalog;

public sealed record TableInfo(long TableId, string TableName, uint RootPageId, TableSchema Schema);
