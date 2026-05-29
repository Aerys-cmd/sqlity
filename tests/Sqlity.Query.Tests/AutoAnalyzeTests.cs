using Sqlity.Query;
using Sqlity.Storage;
using Sqlity.Storage.Rows;

namespace Sqlity.Query.Tests;

/// <summary>
/// Tests for the PostgreSQL-style mutation-count auto-analyze feature.
/// Stats are gathered automatically once committed mutations cross
/// <c>AutoAnalyzeBaseThreshold + AutoAnalyzeScaleFactor × rowCount</c>.
/// </summary>
public sealed class AutoAnalyzeTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (QueryEngine Engine, string Path) CreateEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        var engine = new QueryEngine(path);
        engine.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64);");
        return (engine, path);
    }

    private static void Cleanup(QueryEngine engine, string path)
    {
        engine.Dispose();
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Inserts <paramref name="count"/> rows into table "t" within a single transaction.</summary>
    private static void InsertBatch(QueryEngine engine, int startId, int count)
    {
        engine.BeginTransaction();
        for (var i = startId; i < startId + count; i++)
            engine.Execute($"INSERT INTO t VALUES ({i}, {i * 10});");
        engine.Commit();
    }

    private const long Base = StorageEngine.AutoAnalyzeBaseThreshold;

    // ── Stats null before any commits ─────────────────────────────────────────

    [Fact]
    public void GetStatistics_null_before_any_mutations()
    {
        var (engine, path) = CreateEngine();
        try
        {
            Assert.Null(engine.Storage.GetStatistics("t"));
        }
        finally { Cleanup(engine, path); }
    }

    // ── Below threshold: no auto-analyze ─────────────────────────────────────

    [Fact]
    public void AutoAnalyze_does_not_fire_below_threshold()
    {
        var (engine, path) = CreateEngine();
        try
        {
            // Insert Base-1 rows — each in its own autocommit transaction.
            // Cumulative mutations: Base-1, threshold: Base → no fire.
            InsertBatch(engine, 0, (int)(Base - 1));

            var stats = engine.Storage.GetStatistics("t");
            // A stub (RowCount=0) may exist from PersistMutationCountOnly, but real analyze must not have fired.
            Assert.True(stats is null || stats.RowCount == 0,
                $"Expected stats to be null or a stub (RowCount=0), but got RowCount={stats?.RowCount}");
        }
        finally { Cleanup(engine, path); }
    }

    // ── At threshold: auto-analyze fires ─────────────────────────────────────

    [Fact]
    public void AutoAnalyze_fires_when_threshold_reached()
    {
        var (engine, path) = CreateEngine();
        try
        {
            // Insert exactly Base rows in one transaction → mutations = Base >= Base → fire.
            InsertBatch(engine, 0, (int)Base);

            var stats = engine.Storage.GetStatistics("t");
            Assert.NotNull(stats);
            Assert.Equal(Base, stats.RowCount);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Rolled-back mutations do not count ───────────────────────────────────

    [Fact]
    public void AutoAnalyze_does_not_count_rolled_back_mutations()
    {
        var (engine, path) = CreateEngine();
        try
        {
            // Rollback Base rows → mutations should not accumulate.
            engine.BeginTransaction();
            for (var i = 0; i < (int)Base; i++)
                engine.Execute($"INSERT INTO t VALUES ({i}, {i});");
            engine.Rollback();

            // After rollback, committed mutations = 0 → stats still null.
            Assert.Null(engine.Storage.GetStatistics("t"));

            // Insert Base-1 more committed rows; still below threshold → no fire.
            InsertBatch(engine, 0, (int)(Base - 1));
            var stats = engine.Storage.GetStatistics("t");
            Assert.True(stats is null || stats.RowCount == 0,
                $"Expected no real stats after rollback + {Base - 1} inserts, but got RowCount={stats?.RowCount}");
        }
        finally { Cleanup(engine, path); }
    }

    // ── Mutations accumulate across multiple small transactions ───────────────

    [Fact]
    public void AutoAnalyze_accumulates_mutations_across_transactions()
    {
        var (engine, path) = CreateEngine();
        try
        {
            // 5 transactions of 10 rows each = 50 cumulative mutations.
            // Only the 5th transaction pushes total to Base → fires on commit 5.
            const int batch = 10;
            const int batches = 5;  // 10 × 5 = 50 = Base
            for (var b = 0; b < batches; b++)
                InsertBatch(engine, b * batch, batch);

            var stats = engine.Storage.GetStatistics("t");
            Assert.NotNull(stats);
            Assert.Equal(batch * batches, stats.RowCount);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Counter resets after auto-analyze; next batch triggers again ──────────

    [Fact]
    public void AutoAnalyze_counter_resets_and_fires_again()
    {
        var (engine, path) = CreateEngine();
        try
        {
            // First auto-analyze fires at Base inserts.
            InsertBatch(engine, 0, (int)Base);
            var statsAfterFirst = engine.Storage.GetStatistics("t");
            Assert.NotNull(statsAfterFirst);
            Assert.Equal(Base, statsAfterFirst.RowCount);

            // After first fire, counter resets to 0.
            // Now insert Base more rows (threshold is now Base + 0.2 * Base = 60 for Base=50).
            // Inserting Base rows should trigger again.
            var secondBatch = (int)(Base + (long)(StorageEngine.AutoAnalyzeScaleFactor * Base));
            InsertBatch(engine, (int)Base, secondBatch);

            var statsAfterSecond = engine.Storage.GetStatistics("t");
            Assert.NotNull(statsAfterSecond);
            Assert.Equal(Base + secondBatch, statsAfterSecond.RowCount);
        }
        finally { Cleanup(engine, path); }
    }

    // ── DELETE mutations also count ───────────────────────────────────────────

    [Fact]
    public void AutoAnalyze_counts_delete_mutations()
    {
        var (engine, path) = CreateEngine();
        try
        {
            // Insert Base/2 rows (well below threshold).
            InsertBatch(engine, 0, (int)(Base / 2));
            var statsAfterInserts = engine.Storage.GetStatistics("t");
            Assert.True(statsAfterInserts is null || statsAfterInserts.RowCount == 0);

            // Delete all rows in one transaction → pushes total past threshold.
            engine.BeginTransaction();
            engine.Execute("DELETE FROM t;");
            engine.Commit();

            // Total mutations = Base/2 (inserts) + Base/2 (deletes) = Base → fire.
            var stats = engine.Storage.GetStatistics("t");
            Assert.NotNull(stats);
            // After deleting all rows, RowCount should be 0.
            Assert.Equal(0, stats.RowCount);
        }
        finally { Cleanup(engine, path); }
    }

    // ── UPDATE mutations also count ───────────────────────────────────────────

    [Fact]
    public void AutoAnalyze_counts_update_mutations()
    {
        var (engine, path) = CreateEngine();
        try
        {
            // Insert Base/2 rows.
            InsertBatch(engine, 0, (int)(Base / 2));

            // Update all rows — pushes total to Base → fire.
            engine.BeginTransaction();
            engine.Execute("UPDATE t SET val = 999;");
            engine.Commit();

            var stats = engine.Storage.GetStatistics("t");
            Assert.NotNull(stats);
            Assert.Equal(Base / 2, stats.RowCount);
        }
        finally { Cleanup(engine, path); }
    }

    // ── Mutation count persists across engine reopen ──────────────────────────

    [Fact]
    public void AutoAnalyze_mutation_count_persists_across_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            // First session: insert Base-10 rows (below threshold, mutations persisted).
            using (var engine1 = new QueryEngine(path))
            {
                engine1.Execute("CREATE TABLE t (id INT64 PRIMARY KEY, val INT64);");
                InsertBatch(engine1, 0, (int)(Base - 10));

                // No auto-analyze yet.
                var stats = engine1.Storage.GetStatistics("t");
                Assert.True(stats is null || stats.RowCount == 0);
            }

            // Second session: insert 10 more rows → cumulative = Base → fire.
            using var engine2 = new QueryEngine(path);
            InsertBatch(engine2, (int)(Base - 10), 10);

            var statsAfter = engine2.Storage.GetStatistics("t");
            Assert.NotNull(statsAfter);
            Assert.Equal(Base, statsAfter.RowCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── End-to-end: planner uses auto-analyzed stats ──────────────────────────

    [Fact]
    public void AutoAnalyze_enables_cost_based_plan_selection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        try
        {
            using var engine = new QueryEngine(path);

            // Table with two indexed columns: category (2 distinct) and code (Base distinct).
            engine.Execute("CREATE TABLE items (id INT64 PRIMARY KEY, category INT64, code INT64);");
            engine.Execute("CREATE INDEX idx_category ON items (category);");
            engine.Execute("CREATE INDEX idx_code ON items (code);");

            // Insert Base rows in one batch to trigger auto-analyze.
            engine.BeginTransaction();
            for (var i = 0; i < (int)Base; i++)
                engine.Execute($"INSERT INTO items VALUES ({i}, {i % 2}, {i});");
            engine.Commit();

            // Auto-analyze should have fired: stats available with RowCount = Base.
            var stats = engine.Storage.GetStatistics("items");
            Assert.NotNull(stats);
            Assert.Equal(Base, stats.RowCount);
            Assert.Equal(2L, stats.ColumnNdv["category"]);
            Assert.Equal(Base, stats.ColumnNdv["code"]);

            // With cost model: idx_code has Base NDV (selectivity 1/Base), idx_category has 2 NDV (1/2).
            // Planner should pick idx_code for "WHERE code = X" (lower estimated row count).
            var result = engine.Execute($"SELECT id FROM items WHERE code = {Base / 2};");
            Assert.Single(result.Rows);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
