using Sqlity.Query;
using Sqlity.Storage;
using Sqlity.Storage.Catalog;
using Sqlity.Storage.Statistics;

namespace Sqlity.Studio.Services;

/// <summary>
/// Thin session wrapper that owns exactly one StorageEngine + QueryEngine pair
/// and serializes all engine calls behind a semaphore.
/// </summary>
public sealed class SqlitySession : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly StorageEngine _storage;
    private readonly QueryEngine _engine;
    private bool _disposed;

    public string FilePath { get; }
    public bool InTransaction => _engine.InTransaction;

    public SqlitySession(string filePath)
    {
        FilePath = filePath;
        _storage = StorageEngine.Open(filePath);
        _engine = new QueryEngine(_storage);
    }

    public async Task<QueryExecutionResult> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(() => _engine.Execute(sql), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyList<TableInfo> ListUserTableInfos() =>
        _storage.ListTables()
            .Where(t => !t.TableName.StartsWith("__sqlity_", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public IReadOnlyList<IndexInfo> ListAllIndexes() =>
        _storage.ListIndexes()
            .Where(i => !i.TableName.StartsWith("__sqlity_", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public IReadOnlyList<ViewInfo> ListViews() => _storage.GetAllViews();

    public TableInfo GetTableInfo(string tableName) => _storage.GetTable(tableName);

    public IReadOnlyList<IndexInfo> GetIndexesForTable(string tableName) =>
        _storage.GetIndexesForTable(tableName);

    public TableStatistics? GetStatistics(string tableName) =>
        _storage.GetStatistics(tableName);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_engine.InTransaction)
        {
            try { _engine.Rollback(); } catch { }
        }
        _engine.Dispose();
        _storage.Dispose();
    }
}
