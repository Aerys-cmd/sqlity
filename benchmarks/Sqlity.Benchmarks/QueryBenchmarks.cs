using BenchmarkDotNet.Attributes;
using Sqlity.Query;
using Sqlity.Storage;

namespace Sqlity.Benchmarks;

/// <summary>
/// Benchmarks the QueryEngine layer end-to-end (SQL parsing + B-tree execution).
/// INSERT SQL strings are pre-built in setup so string allocation is excluded from the
/// measured path.
/// </summary>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private const string CreateTableSql =
        "CREATE TABLE bench (id INT64 PRIMARY KEY, name STRING);";

    [Params(100, 1_000, 10_000, 100_000)]
    public int N { get; set; }

    [Params("memory", "file")]
    public string Mode { get; set; } = "memory";

    // ── BulkInsertViaSql ──────────────────────────────────────────────────────

    private QueryEngine _insertEngine = null!;
    private StorageEngine? _insertStorage;
    private string? _insertTempFile;
    private string[] _insertSqls = [];

    [IterationSetup(Target = nameof(BulkInsertViaSql))]
    public void SetupBulkInsertViaSql()
    {
        (_insertEngine, _insertStorage, _insertTempFile) = CreateEngine();
        _insertEngine.Execute(CreateTableSql);

        _insertSqls = new string[N];
        for (var i = 0; i < N; i++)
        {
            _insertSqls[i] = $"INSERT INTO bench VALUES ({i}, 'Ada');";
        }
    }

    [Benchmark]
    public void BulkInsertViaSql()
    {
        for (var i = 0; i < N; i++)
        {
            _insertEngine.Execute(_insertSqls[i]);
        }
    }

    [IterationCleanup(Target = nameof(BulkInsertViaSql))]
    public void CleanupBulkInsertViaSql() => DisposeEngine(_insertEngine, _insertStorage, _insertTempFile);

    // ── SelectAllViaSql ───────────────────────────────────────────────────────

    private QueryEngine _selectEngine = null!;
    private StorageEngine? _selectStorage;
    private string? _selectTempFile;

    [GlobalSetup(Target = nameof(SelectAllViaSql))]
    public void SetupSelectAllViaSql()
    {
        (_selectEngine, _selectStorage, _selectTempFile) = CreateEngine();
        _selectEngine.Execute(CreateTableSql);
        for (var i = 0; i < N; i++)
        {
            _selectEngine.Execute($"INSERT INTO bench VALUES ({i}, 'Ada');");
        }
    }

    [Benchmark]
    public QueryExecutionResult SelectAllViaSql() =>
        _selectEngine.Execute("SELECT id, name FROM bench;");

    [GlobalCleanup(Target = nameof(SelectAllViaSql))]
    public void CleanupSelectAllViaSql() => DisposeEngine(_selectEngine, _selectStorage, _selectTempFile);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (QueryEngine engine, StorageEngine? storage, string? tempFile) CreateEngine()
    {
        if (Mode == "memory")
        {
            var pager = new InMemoryPager();
            pager.InitializeNew();
            var storage = new StorageEngine(pager);
            var engine = new QueryEngine(storage);
            return (engine, storage, null);
        }

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        return (new QueryEngine(path), null, path);
    }

    private static void DisposeEngine(QueryEngine engine, StorageEngine? storage, string? tempFile)
    {
        engine.Dispose();
        storage?.Dispose();
        if (tempFile is not null && File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }
    }
}
