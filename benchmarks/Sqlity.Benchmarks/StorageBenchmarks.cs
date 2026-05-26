using BenchmarkDotNet.Attributes;
using Sqlity.Storage;
using Sqlity.Storage.Rows;

namespace Sqlity.Benchmarks;

/// <summary>
/// Benchmarks the StorageEngine layer directly, bypassing SQL parsing.
/// Measures B-tree insert, full table scan, and primary-key point lookup.
/// </summary>
[MemoryDiagnoser]
public class StorageBenchmarks
{
    private static readonly TableSchema Schema = new(
        "bench",
        new ColumnDefinition[]
        {
            new("id", ColumnType.Int64, IsNullable: false),
            new("name", ColumnType.String),
        },
        primaryKeyOrdinal: 0);

    [Params(100, 1_000, 10_000, 100_000)]
    public int N { get; set; }

    [Params("memory", "file")]
    public string Mode { get; set; } = "memory";

    // ── BulkInsert ────────────────────────────────────────────────────────────

    private StorageEngine _insertEngine = null!;
    private string? _insertTempFile;

    [IterationSetup(Target = nameof(BulkInsert))]
    public void SetupBulkInsert()
    {
        (_insertEngine, _insertTempFile) = CreateStorage();
        _insertEngine.CreateTable(Schema);
    }

    [Benchmark]
    public void BulkInsert()
    {
        for (var i = 0; i < N; i++)
        {
            _insertEngine.Insert("bench", new object?[] { (long)i, "Ada" });
        }
    }

    [IterationCleanup(Target = nameof(BulkInsert))]
    public void CleanupBulkInsert() => DisposeStorage(_insertEngine, _insertTempFile);

    // ── FullScan ──────────────────────────────────────────────────────────────

    private StorageEngine _scanEngine = null!;
    private string? _scanTempFile;

    [GlobalSetup(Target = nameof(FullScan))]
    public void SetupFullScan()
    {
        (_scanEngine, _scanTempFile) = CreateStorage();
        _scanEngine.CreateTable(Schema);
        for (var i = 0; i < N; i++)
        {
            _scanEngine.Insert("bench", new object?[] { (long)i, "Ada" });
        }
    }

    [Benchmark]
    public IReadOnlyList<object?[]> FullScan() => _scanEngine.ReadAll("bench");

    [GlobalCleanup(Target = nameof(FullScan))]
    public void CleanupFullScan() => DisposeStorage(_scanEngine, _scanTempFile);

    // ── PrimaryKeyLookup ──────────────────────────────────────────────────────

    private StorageEngine _lookupEngine = null!;
    private string? _lookupTempFile;

    [GlobalSetup(Target = nameof(PrimaryKeyLookup))]
    public void SetupPrimaryKeyLookup()
    {
        (_lookupEngine, _lookupTempFile) = CreateStorage();
        _lookupEngine.CreateTable(Schema);
        for (var i = 0; i < N; i++)
        {
            _lookupEngine.Insert("bench", new object?[] { (long)i, "Ada" });
        }
    }

    [Benchmark]
    public bool PrimaryKeyLookup() => _lookupEngine.TryReadByPrimaryKey("bench", N / 2, out _);

    [GlobalCleanup(Target = nameof(PrimaryKeyLookup))]
    public void CleanupPrimaryKeyLookup() => DisposeStorage(_lookupEngine, _lookupTempFile);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (StorageEngine engine, string? tempFile) CreateStorage()
    {
        if (Mode == "memory")
        {
            var pager = new InMemoryPager();
            pager.InitializeNew();
            return (new StorageEngine(pager), null);
        }

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlity");
        return (StorageEngine.Open(path), path);
    }

    private static void DisposeStorage(StorageEngine engine, string? tempFile)
    {
        engine.Dispose();
        if (tempFile is not null && File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }
    }
}
