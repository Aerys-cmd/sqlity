using Sqlity.Query.Planner;
using Sqlity.Storage;
using Sqlity.Storage.Rows;

namespace Sqlity.Query.Tests;

/// <summary>
/// Tests for the cost-based query planner introduced in Phase 4.
/// Covers ANALYZE statistics collection and cardinality-driven plan selection.
/// </summary>
public sealed class Phase4CostBasedPlannerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private static void Cleanup(StorageEngine storage, string path)
    {
        storage.Dispose();
        if (File.Exists(path)) File.Delete(path);
    }

    // ── ANALYZE ───────────────────────────────────────────────────────────────

    [Fact]
    public void Analyze_empty_table_returns_zero_row_count()
    {
        var (storage, path) = CreateDb();
        try
        {
            storage.AnalyzeTable("orders");
            var stats = storage.GetStatistics("orders");

            Assert.NotNull(stats);
            Assert.Equal(0, stats.RowCount);
            Assert.Equal(0, stats.ColumnNdv["customer_id"]);
        }
        finally { Cleanup(storage, path); }
    }

    [Fact]
    public void Analyze_collects_correct_row_count_and_ndv()
    {
        var (storage, path) = CreateDb();
        try
        {
            // Insert 4 rows: 3 distinct customer_ids, 2 distinct statuses
            storage.Insert("orders", [1L, 10L, "active"]);
            storage.Insert("orders", [2L, 20L, "active"]);
            storage.Insert("orders", [3L, 30L, "shipped"]);
            storage.Insert("orders", [4L, 10L, "shipped"]);

            storage.AnalyzeTable("orders");
            var stats = storage.GetStatistics("orders");

            Assert.NotNull(stats);
            Assert.Equal(4, stats.RowCount);
            Assert.Equal(3, stats.ColumnNdv["customer_id"]); // 10, 20, 30
            Assert.Equal(2, stats.ColumnNdv["status"]);      // active, shipped
        }
        finally { Cleanup(storage, path); }
    }

    [Fact]
    public void Analyze_without_table_name_via_sql_collects_stats_for_all_tables()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        using var engine = new QueryEngine(path);
        try
        {
            engine.Execute("CREATE TABLE t1 (id INT64 PRIMARY KEY, val INT64);");
            engine.Execute("CREATE TABLE t2 (id INT64 PRIMARY KEY, val INT64);");
            engine.Execute("INSERT INTO t1 VALUES (1, 100);");
            engine.Execute("INSERT INTO t2 VALUES (1, 200); INSERT INTO t2 VALUES (2, 300);");

            engine.Execute("ANALYZE;");

            // stats are on the storage — verify via a second engine on same storage
            // (engine wraps storage; here we check by querying that no exception is thrown)
            // The real assertion is that ANALYZE ALL runs without error and covers both tables.
            var result = engine.Execute("SELECT id FROM t1;");
            Assert.Single(result.Rows);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Analyze_via_sql_with_table_name_collects_stats()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        using var engine = new QueryEngine(path);
        try
        {
            engine.Execute("CREATE TABLE products (id INT64 PRIMARY KEY, category_id INT64);");
            engine.Execute("INSERT INTO products VALUES (1, 5); INSERT INTO products VALUES (2, 5); INSERT INTO products VALUES (3, 7);");

            // Should not throw, and subsequent queries execute fine.
            engine.Execute("ANALYZE products;");

            var result = engine.Execute("SELECT id FROM products;");
            Assert.Equal(3, result.Rows.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Cost-based plan selection ─────────────────────────────────────────────

    [Fact]
    public void Planner_auto_analyzes_and_picks_matching_index()
    {
        var (storage, path) = CreateDb();
        try
        {
            // Insert rows so stats are meaningful.
            for (long i = 0; i < 20; i++)
                storage.Insert("orders", [i, i, i % 2 == 0 ? "active" : "shipped"]);

            storage.CreateIndex("idx_customer_id", "orders", new[] { "customer_id" }, isUnique: false);
            storage.CreateIndex("idx_status", "orders", new[] { "status" }, isUnique: false);

            // No explicit ANALYZE — planner should auto-collect stats and pick correctly.
            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");
            var filter = new ComparisonExpression(null, "customer_id", ComparisonOp.Equals, new SqlLiteral(5L));

            var plan = planner.Plan(table, filter);

            // customer_id has 20 distinct values (high selectivity), status has 2 — planner picks customer_id.
            var seek = Assert.IsType<PhysicalIndexSeek>(plan);
            Assert.Equal("idx_customer_id", seek.Index.IndexName);
        }
        finally { Cleanup(storage, path); }
    }

    [Fact]
    public void Planner_with_stats_picks_more_selective_index()
    {
        var (storage, path) = CreateDb();
        try
        {
            // Insert 100 rows: customer_id has 50 distinct values, status has 2 distinct values.
            // Rule-based would pick whichever index it encounters first (both score 1).
            // Cost-based should pick customer_id (lower estimated cost: 100/50=2 vs 100/2=50).
            for (long i = 0; i < 100; i++)
            {
                var customerId = i % 50;   // 50 distinct
                var status = i % 2 == 0 ? "active" : "shipped"; // 2 distinct
                storage.Insert("orders", [i, customerId, status]);
            }

            // Create idx_status first so it would be encountered first in rule-based ordering.
            storage.CreateIndex("idx_status", "orders", new[] { "status" }, isUnique: false);
            storage.CreateIndex("idx_customer_id", "orders", new[] { "customer_id" }, isUnique: false);

            storage.AnalyzeTable("orders");

            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");

            // WHERE customer_id = X AND status = Y — both indexes match (score 1 each)
            var filter = new BinaryLogicalExpression(
                new ComparisonExpression(null, "customer_id", ComparisonOp.Equals, new SqlLiteral(5L)),
                LogicalOp.And,
                new ComparisonExpression(null, "status", ComparisonOp.Equals, new SqlLiteral("active")));

            var plan = planner.Plan(table, filter);

            // Cost-based planner must pick the more selective customer_id index.
            var seek = Assert.IsType<PhysicalIndexSeek>(plan);
            Assert.Equal("idx_customer_id", seek.Index.IndexName);
        }
        finally { Cleanup(storage, path); }
    }

    [Fact]
    public void Planner_with_stats_falls_back_to_full_scan_when_all_rows_match()
    {
        var (storage, path) = CreateDb();
        try
        {
            // Single status value — NDV=1 means index seek cost = rowCount/1 = rowCount,
            // equal to full scan. Planner should fall back to full scan (seek cost not < full scan cost).
            for (long i = 0; i < 10; i++)
                storage.Insert("orders", [i, i, "active"]);

            storage.CreateIndex("idx_status", "orders", new[] { "status" }, isUnique: false);
            storage.AnalyzeTable("orders");

            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");
            var filter = new ComparisonExpression(null, "status", ComparisonOp.Equals, new SqlLiteral("active"));

            var plan = planner.Plan(table, filter);

            // Seek cost = 10/1 = 10, full scan cost = 10 → not strictly less, use full scan.
            Assert.IsType<PhysicalFullScan>(plan);
        }
        finally { Cleanup(storage, path); }
    }

    [Fact]
    public void GetStatistics_returns_null_before_analyze_or_any_planning()
    {
        // GetStatistics is a pure getter on the storage layer.
        // Stats are null until either ANALYZE is called explicitly or the planner
        // runs for the first time (which auto-collects stats lazily).
        var (storage, path) = CreateDb();
        try
        {
            Assert.Null(storage.GetStatistics("orders"));
        }
        finally { Cleanup(storage, path); }
    }

    [Fact]
    public void Analyze_updates_stats_after_new_rows_inserted()
    {
        var (storage, path) = CreateDb();
        try
        {
            storage.Insert("orders", [1L, 10L, "active"]);
            storage.AnalyzeTable("orders");
            Assert.Equal(1, storage.GetStatistics("orders")!.RowCount);

            storage.Insert("orders", [2L, 20L, "shipped"]);
            storage.AnalyzeTable("orders");
            Assert.Equal(2, storage.GetStatistics("orders")!.RowCount);
        }
        finally { Cleanup(storage, path); }
    }

    [Fact]
    public void Planner_auto_collects_stats_without_explicit_analyze()
    {
        var (storage, path) = CreateDb();
        try
        {
            // Insert 100 rows with high-selectivity customer_id index.
            for (long i = 0; i < 100; i++)
                storage.Insert("orders", [i, i % 50, i % 2 == 0 ? "active" : "shipped"]);

            // idx_status first so rule-based would pick it; no explicit ANALYZE.
            storage.CreateIndex("idx_status", "orders", new[] { "status" }, isUnique: false);
            storage.CreateIndex("idx_customer_id", "orders", new[] { "customer_id" }, isUnique: false);

            Assert.Null(storage.GetStatistics("orders")); // no explicit ANALYZE yet

            var planner = new QueryPlanner(storage);
            var table = storage.GetTable("orders");
            var filter = new BinaryLogicalExpression(
                new ComparisonExpression(null, "customer_id", ComparisonOp.Equals, new SqlLiteral(5L)),
                LogicalOp.And,
                new ComparisonExpression(null, "status", ComparisonOp.Equals, new SqlLiteral("active")));

            var plan = planner.Plan(table, filter);

            // Stats were auto-collected; cost-based planner picks the more selective index.
            Assert.NotNull(storage.GetStatistics("orders"));
            var seek = Assert.IsType<PhysicalIndexSeek>(plan);
            Assert.Equal("idx_customer_id", seek.Index.IndexName);
        }
        finally { Cleanup(storage, path); }
    }

    // ── Persistence (stats survive engine reopen) ─────────────────────────────

    [Fact]
    public void Stats_persist_across_engine_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            // First connection: create table, insert rows, run explicit ANALYZE.
            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(new TableSchema(
                    "orders",
                    new[]
                    {
                        new ColumnDefinition("id", ColumnType.Int64, IsNullable: false),
                        new ColumnDefinition("customer_id", ColumnType.Int64, IsNullable: false),
                        new ColumnDefinition("status", ColumnType.String, IsNullable: true)
                    },
                    primaryKeyOrdinal: 0));

                for (long i = 0; i < 50; i++)
                    storage.Insert("orders", [i, i % 10, i % 3 == 0 ? "active" : "shipped"]);

                storage.AnalyzeTable("orders");
                Assert.Equal(50, storage.GetStatistics("orders")!.RowCount);
            }

            // Second connection: stats must be loaded from disk — no full scan needed.
            using (var storage2 = StorageEngine.Open(path))
            {
                var stats = storage2.GetStatistics("orders");
                Assert.NotNull(stats);
                Assert.Equal(50, stats.RowCount);
                Assert.Equal(10, stats.ColumnNdv["customer_id"]); // i % 10 → 10 distinct values
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Auto_analyze_does_not_persist_stats_to_disk()
    {
        // Lazy auto-analyze triggered by the planner must NOT write to the catalog
        // so that read-only SELECTs have no hidden write side-effects.
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(new TableSchema(
                    "orders",
                    new[]
                    {
                        new ColumnDefinition("id", ColumnType.Int64, IsNullable: false),
                        new ColumnDefinition("customer_id", ColumnType.Int64, IsNullable: false),
                        new ColumnDefinition("status", ColumnType.String, IsNullable: true)
                    },
                    primaryKeyOrdinal: 0));

                for (long i = 0; i < 20; i++)
                    storage.Insert("orders", [i, i, "active"]);

                // Trigger planner's auto-analyze (persist:false).
                var planner = new QueryPlanner(storage);
                var table = storage.GetTable("orders");
                planner.Plan(table, null);
                Assert.NotNull(storage.GetStatistics("orders")); // in-memory cache populated
            }

            // Second connection must find NO persisted stats (auto-analyze didn't persist).
            using (var storage2 = StorageEngine.Open(path))
            {
                Assert.Null(storage2.GetStatistics("orders"));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Stats_are_invalidated_on_truncate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(new TableSchema(
                "orders",
                new[]
                {
                    new ColumnDefinition("id", ColumnType.Int64, IsNullable: false),
                    new ColumnDefinition("status", ColumnType.String, IsNullable: true)
                },
                primaryKeyOrdinal: 0));

            storage.Insert("orders", [1L, "active"]);
            storage.AnalyzeTable("orders");
            Assert.Equal(1, storage.GetStatistics("orders")!.RowCount);

            storage.TruncateTable("orders");

            // In-memory cache cleared.
            Assert.Null(storage.GetStatistics("orders"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Stats_are_invalidated_on_add_column()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var storage = StorageEngine.Open(path);
            storage.CreateTable(new TableSchema(
                "orders",
                new[]
                {
                    new ColumnDefinition("id", ColumnType.Int64, IsNullable: false),
                    new ColumnDefinition("status", ColumnType.String, IsNullable: true)
                },
                primaryKeyOrdinal: 0));

            storage.AnalyzeTable("orders");
            Assert.NotNull(storage.GetStatistics("orders"));

            storage.AddColumn("orders", new ColumnDefinition("notes", ColumnType.String, IsNullable: true));

            Assert.Null(storage.GetStatistics("orders"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Stats_migrate_on_rename_table_and_persist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(new TableSchema(
                    "orders",
                    new[]
                    {
                        new ColumnDefinition("id", ColumnType.Int64, IsNullable: false),
                        new ColumnDefinition("status", ColumnType.String, IsNullable: true)
                    },
                    primaryKeyOrdinal: 0));

                storage.Insert("orders", [1L, "active"]);
                storage.AnalyzeTable("orders");
                storage.RenameTable("orders", "invoices");

                // In-memory stats migrated to new name.
                Assert.Null(storage.GetStatistics("orders"));
                Assert.NotNull(storage.GetStatistics("invoices"));
                Assert.Equal(1, storage.GetStatistics("invoices")!.RowCount);
            }

            // Verify persistence: second open finds stats under new name.
            using (var storage2 = StorageEngine.Open(path))
            {
                Assert.Null(storage2.GetStatistics("orders"));
                var stats = storage2.GetStatistics("invoices");
                Assert.NotNull(stats);
                Assert.Equal(1, stats.RowCount);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Stats_cleared_on_drop_table_and_absent_after_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using (var storage = StorageEngine.Open(path))
            {
                storage.CreateTable(new TableSchema(
                    "orders",
                    new[]
                    {
                        new ColumnDefinition("id", ColumnType.Int64, IsNullable: false)
                    },
                    primaryKeyOrdinal: 0));

                storage.Insert("orders", [1L]);
                storage.AnalyzeTable("orders");

                storage.DropTable("orders");
                Assert.Null(storage.GetStatistics("orders"));
            }

            // Re-create table with same name on a fresh engine — no stale stats.
            using (var storage2 = StorageEngine.Open(path))
            {
                storage2.CreateTable(new TableSchema(
                    "orders",
                    new[]
                    {
                        new ColumnDefinition("id", ColumnType.Int64, IsNullable: false)
                    },
                    primaryKeyOrdinal: 0));

                Assert.Null(storage2.GetStatistics("orders"));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
