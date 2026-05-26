using Sqlity.Query.Planner;
using Sqlity.Storage;
using Sqlity.Storage.Rows;

namespace Sqlity.Query.Tests;

/// <summary>
/// Tests the rule-based QueryPlanner in isolation using a real StorageEngine backed by a temp file.
/// We verify which physical plan is chosen for a given WHERE clause and set of indexes.
/// </summary>
public sealed class QueryPlannerTests
{
    private static (StorageEngine Storage, string Path) CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        var storage = StorageEngine.Open(path);
        storage.CreateTable(new TableSchema(
            "orders",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64, IsNullable: false),
                new ColumnDefinition("customer_id", ColumnType.Int64, IsNullable: false),
                new ColumnDefinition("status", ColumnType.String, IsNullable: true)
            },
            primaryKeyOrdinal: 0));
        return (storage, path);
    }

    [Fact]
    public void Plan_without_index_returns_full_scan()
    {
        var (storage, path) = CreateDb();
        try
        {
            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");
            var filter = new ComparisonExpression(null, "customer_id", ComparisonOp.Equals, new SqlLiteral(42L));

            var plan = planner.Plan(table, filter);

            Assert.IsType<PhysicalFullScan>(plan);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Plan_with_matching_index_returns_index_seek()
    {
        var (storage, path) = CreateDb();
        try
        {
            storage.CreateIndex("idx_customer_id", "orders", new[] { "customer_id" }, isUnique: false);

            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");
            var filter = new ComparisonExpression(null, "customer_id", ComparisonOp.Equals, new SqlLiteral(42L));

            var plan = planner.Plan(table, filter);

            var seekPlan = Assert.IsType<PhysicalIndexSeek>(plan);
            Assert.Equal("idx_customer_id", seekPlan.Index.IndexName);
            Assert.Null(seekPlan.PostFilter);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Plan_with_null_filter_returns_full_scan_no_filter()
    {
        var (storage, path) = CreateDb();
        try
        {
            storage.CreateIndex("idx_customer_id", "orders", new[] { "customer_id" }, isUnique: false);

            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");

            var plan = planner.Plan(table, null);

            var scanPlan = Assert.IsType<PhysicalFullScan>(plan);
            Assert.Null(scanPlan.Filter);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Plan_compound_AND_uses_index_for_leading_column_and_post_filters_rest()
    {
        var (storage, path) = CreateDb();
        try
        {
            storage.CreateIndex("idx_customer_id", "orders", new[] { "customer_id" }, isUnique: false);

            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");

            // customer_id = 42 AND status = 'shipped'
            var filter = new BinaryLogicalExpression(
                new ComparisonExpression(null, "customer_id", ComparisonOp.Equals, new SqlLiteral(42L)),
                LogicalOp.And,
                new ComparisonExpression(null, "status", ComparisonOp.Equals, new SqlLiteral("shipped")));

            var plan = planner.Plan(table, filter);

            var seekPlan = Assert.IsType<PhysicalIndexSeek>(plan);
            Assert.Equal("idx_customer_id", seekPlan.Index.IndexName);
            // status predicate must survive as post-filter
            Assert.NotNull(seekPlan.PostFilter);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Plan_selects_best_index_when_multiple_candidates_exist()
    {
        var (storage, path) = CreateDb();
        try
        {
            // Two single-column indexes; planner should pick the one that matches the WHERE
            storage.CreateIndex("idx_customer_id", "orders", new[] { "customer_id" }, isUnique: false);
            storage.CreateIndex("idx_status", "orders", new[] { "status" }, isUnique: false);

            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");
            var filter = new ComparisonExpression(null, "customer_id", ComparisonOp.Equals, new SqlLiteral(42L));

            var plan = planner.Plan(table, filter);

            var seekPlan = Assert.IsType<PhysicalIndexSeek>(plan);
            Assert.Equal("idx_customer_id", seekPlan.Index.IndexName);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Plan_non_equality_predicate_alone_falls_back_to_full_scan()
    {
        var (storage, path) = CreateDb();
        try
        {
            storage.CreateIndex("idx_customer_id", "orders", new[] { "customer_id" }, isUnique: false);

            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");
            // Range predicate only — current MVP does not score non-equality leading columns
            var filter = new ComparisonExpression(null, "customer_id", ComparisonOp.GreaterThan, new SqlLiteral(10L));

            var plan = planner.Plan(table, filter);

            Assert.IsType<PhysicalFullScan>(plan);
        }
        finally
        {
            storage.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
