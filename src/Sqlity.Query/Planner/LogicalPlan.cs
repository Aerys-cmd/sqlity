using Sqlity.Storage.BTree;
using Sqlity.Storage.Catalog;

namespace Sqlity.Query.Planner;

internal abstract record LogicalPlan;

internal sealed record LogicalScan(
    TableInfo Table,
    WhereExpression? Filter) : LogicalPlan;

internal sealed record LogicalIndexSeek(
    TableInfo Table,
    IndexInfo Index,
    IndexSeekRange Range,
    WhereExpression? PostFilter) : LogicalPlan;
