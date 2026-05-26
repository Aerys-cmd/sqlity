using Sqlity.Storage.BTree;
using Sqlity.Storage.Catalog;

namespace Sqlity.Query.Planner;

internal abstract record PhysicalPlan;

internal sealed record PhysicalFullScan(
    TableInfo Table,
    WhereExpression? Filter) : PhysicalPlan;

internal sealed record PhysicalIndexSeek(
    TableInfo Table,
    IndexInfo Index,
    IndexSeekRange Range,
    WhereExpression? PostFilter) : PhysicalPlan;
