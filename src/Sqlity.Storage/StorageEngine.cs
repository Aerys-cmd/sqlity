using Sqlity.Core;
using Sqlity.Storage.Abstractions;
using Sqlity.Storage.BTree;
using Sqlity.Storage.Catalog;
using Sqlity.Storage.IO;
using Sqlity.Storage.Pages;
using Sqlity.Storage.Rows;
using Sqlity.Storage.Statistics;

namespace Sqlity.Storage;

public sealed class StorageEngine : IDisposable
{
    private readonly IPager _pager;
    private readonly bool _ownsPager;
    private readonly CatalogStore _catalog;
    private IndexCatalogStore? _indexCatalog;
    private ViewCatalogStore? _viewCatalog;
    private StatsCatalogStore? _statsCatalog;
    private readonly RowSerializer _rowSerializer = new();

    // Write-through in-memory cache loaded from the stats catalog page on open.
    // Explicit ANALYZE writes to both the cache and the catalog page; auto-analyze
    // (triggered by the query planner) writes only to the cache.
    private readonly Dictionary<string, TableStatistics> _statistics =
        new(StringComparer.OrdinalIgnoreCase);

    public StorageEngine(IPager pager)
        : this(pager, ownsPager: false)
    {
    }

    private StorageEngine(IPager pager, bool ownsPager)
    {
        ArgumentNullException.ThrowIfNull(pager);

        _pager = pager;
        _ownsPager = ownsPager;
        _catalog = new CatalogStore(_pager, EnsureCatalogRootPage());

        var header = _pager.ReadDatabaseHeader();
        if (header.IndexCatalogRootPageId != 0)
            _indexCatalog = new IndexCatalogStore(_pager, header.IndexCatalogRootPageId);
        if (header.ViewCatalogRootPageId != 0)
            _viewCatalog = new ViewCatalogStore(_pager, header.ViewCatalogRootPageId);
        if (header.StatsCatalogRootPageId != 0)
        {
            _statsCatalog = new StatsCatalogStore(_pager, header.StatsCatalogRootPageId);
            LoadStatsFromCatalog();
        }
    }

    /// <summary>
    /// Opens (or creates) a database at <paramref name="filePath"/>.
    /// Pass <c>":memory:"</c> to open a transient in-memory database with no file I/O.
    /// Set <paramref name="useWal"/> to <see langword="true"/> to use Write-Ahead Logging
    /// instead of the default rollback journal durability mode.
    /// </summary>
    public static StorageEngine Open(string filePath, bool useWal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (string.Equals(filePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var memPager = new InMemoryPager();
            memPager.InitializeNew();
            return new StorageEngine(memPager, ownsPager: true);
        }

        if (useWal)
        {
            var walPager = new WalPager(filePath);
            if (new FileInfo(filePath).Length == 0)
                walPager.InitializeNew();
            else
                walPager.RecoverIfNeeded();
            return new StorageEngine(walPager, ownsPager: true);
        }

        var filePager = new FilePager(filePath);
        if (new FileInfo(filePath).Length == 0)
        {
            filePager.InitializeNew();
        }
        else
        {
            filePager.RecoverIfNeeded();
        }

        var pager = new BufferedPager(filePager);
        return new StorageEngine(pager, ownsPager: true);
    }

    public IReadOnlyList<TableInfo> ListTables() => _catalog.ReadTables();

    public IReadOnlyList<IndexInfo> ListIndexes() => _indexCatalog?.ReadAll() ?? [];

    public IReadOnlyList<IndexInfo> GetIndexesForTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return _indexCatalog?.GetByTable(tableName) ?? [];
    }

    // ── Statistics ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="tableName"/> and caches per-table row count and per-column
    /// distinct-value estimates.
    /// </summary>
    /// <param name="tableName">Table to analyze.</param>
    /// <param name="persist">
    /// When <see langword="true"/> (default), stats are written to the
    /// <c>__sqlity_stat1</c> catalog page so they survive connection close and process
    /// restart. Pass <see langword="false"/> for in-memory-only collection (used by the
    /// query planner's lazy auto-analyze, which must not have hidden write side-effects
    /// during a SELECT).
    /// </param>
    public void AnalyzeTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var table = GetTable(tableName);
        var cells = new BPlusTree(_pager, table.RootPageId).ReadAll();

        var ndvSets = table.Schema.Columns
            .Select(_ => new HashSet<object?>())
            .ToArray();

        foreach (var cell in cells)
        {
            var row = _rowSerializer.Read(table.Schema, cell.Payload);
            for (var i = 0; i < row.Length; i++)
                ndvSets[i].Add(row[i]);
        }

        var ndv = table.Schema.Columns
            .Select((col, i) => (col.Name, (long)ndvSets[i].Count))
            .ToDictionary(t => t.Name, t => t.Item2, StringComparer.OrdinalIgnoreCase);

        var stats = new TableStatistics(cells.Count, ndv);
        _statistics[tableName] = stats;

        PersistStats(tableName, stats);
    }

    /// <summary>Analyzes every user table. Equivalent to running <c>ANALYZE</c> without a table name.</summary>
    public void AnalyzeAll()
    {
        foreach (var table in _catalog.ReadTables())
            AnalyzeTable(table.TableName);
    }

    /// <summary>Returns the most recently collected statistics for <paramref name="tableName"/>, or <see langword="null"/> if <c>ANALYZE</c> has not been run yet.</summary>
    public TableStatistics? GetStatistics(string tableName) =>
        _statistics.TryGetValue(tableName, out var stats) ? stats : null;

    private void LoadStatsFromCatalog()
    {
        foreach (var (tableName, stats) in _statsCatalog!.ReadAll())
            _statistics[tableName] = stats;
    }

    /// <summary>
    /// Writes stats to the on-disk catalog (best-effort: failure is silently swallowed
    /// so a full catalog page never crashes a SELECT that triggered auto-analyze).
    /// </summary>
    private void PersistStats(string tableName, TableStatistics stats)
    {
        try
        {
            EnsureStatsCatalog().Upsert(tableName, stats);
        }
        catch (Exception)
        {
            // Best-effort: keep in-memory stats but don't crash on persistence failure.
        }
    }

    private void InvalidateStats(string tableName)
    {
        _statistics.Remove(tableName);
        _statsCatalog?.Delete(tableName);
    }

    public bool InTransaction => _pager.InTransaction;

    public void BeginTransaction() => _pager.BeginTransaction();

    public void Commit() => _pager.Commit();

    public void Rollback() => _pager.Rollback();

    public TableInfo CreateTable(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (_catalog.TryGetByName(schema.TableName, out _))
        {
            throw new InvalidOperationException($"Table '{schema.TableName}' already exists.");
        }

        var rootPageId = _pager.AllocatePage(PageType.TableLeaf);
        var insertStatus = _catalog.TryInsert(schema, rootPageId, out var table);

        if (insertStatus == CatalogInsertStatus.CatalogFull)
        {
            _pager.ReleasePage(rootPageId);
            throw new InvalidOperationException("The catalog page is full. Page splits are not implemented yet.");
        }

        if (insertStatus == CatalogInsertStatus.DuplicateTableName)
        {
            _pager.ReleasePage(rootPageId);
            throw new InvalidOperationException($"Table '{schema.TableName}' already exists.");
        }

        IncrementSchemaVersion();
        return table!;
    }

    /// <summary>
    /// Creates a secondary index and back-fills it with any existing rows in the table.
    /// </summary>
    public IndexInfo CreateIndex(string indexName, string tableName, IReadOnlyList<string> columns, bool isUnique)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
            throw new InvalidOperationException("CREATE INDEX requires at least one column.");

        var table = GetTable(tableName);
        foreach (var col in columns)
        {
            if (!table.Schema.TryGetColumnOrdinal(col, out _))
                throw new InvalidOperationException($"Column '{col}' does not exist in table '{tableName}'.");
        }

        var indexCatalog = EnsureIndexCatalog();
        if (indexCatalog.TryGetByName(indexName, out _))
            throw new InvalidOperationException($"Index '{indexName}' already exists.");

        var rootPageId = _pager.AllocatePage(PageType.IndexLeaf);
        var status = indexCatalog.TryInsert(indexName, tableName, rootPageId, columns, isUnique, out var index);

        if (status == IndexCatalogInsertStatus.CatalogFull)
        {
            _pager.ReleasePage(rootPageId);
            throw new InvalidOperationException("The index catalog page is full.");
        }

        if (status == IndexCatalogInsertStatus.DuplicateIndexName)
        {
            _pager.ReleasePage(rootPageId);
            throw new InvalidOperationException($"Index '{indexName}' already exists.");
        }

        // Back-fill existing rows.
        var columnOrdinals = columns.Select(c => table.Schema.GetColumnOrdinal(c)).ToList();
        var tree = new SecondaryBPlusTree(_pager, rootPageId);

        foreach (var cell in new BPlusTree(_pager, table.RootPageId).ReadAll())
        {
            var rowValues = _rowSerializer.Read(table.Schema, cell.Payload);
            InsertIndexEntry(tree, table.Schema.Columns, columnOrdinals, rowValues, cell.PrimaryKey, isUnique);
        }

        IncrementSchemaVersion();
        return index!;
    }

    public TableInfo GetTable(string tableName)
    {
        if (!_catalog.TryGetByName(tableName, out var table))
        {
            throw new InvalidOperationException($"Table '{tableName}' does not exist.");
        }

        return table!;
    }

    public void DropTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var table = GetTable(tableName);

        // Drop all secondary indexes for this table first.
        var indexes = GetIndexesForTable(tableName);
        foreach (var index in indexes)
        {
            new SecondaryBPlusTree(_pager, index.RootPageId).ReleaseAllPages();
            _indexCatalog!.Delete(index.IndexId);
        }

        // Release all table data pages.
        new BPlusTree(_pager, table.RootPageId).ReleaseAllPages();

        // Remove the table from the catalog.
        _catalog.Delete(table.TableId);

        // Invalidate any persisted stats for this table.
        InvalidateStats(tableName);

        IncrementSchemaVersion();
    }

    public void RenameTable(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var table = GetTable(oldName);

        if (_catalog.TryGetByName(newName, out _))
            throw new InvalidOperationException($"Table '{newName}' already exists.");

        // Rebuild the schema with the new table name.
        var newSchema = new TableSchema(newName, table.Schema.Columns, table.Schema.PrimaryKeyOrdinal);
        _catalog.Update(new TableInfo(table.TableId, newName, table.RootPageId, newSchema));

        // Update every index that referenced the old table name.
        if (_indexCatalog is not null)
        {
            foreach (var index in _indexCatalog.GetByTable(oldName))
                _indexCatalog.UpdateEntry(index with { TableName = newName });
        }

        // Migrate stats to the new table name.
        if (_statistics.Remove(oldName, out var statsToRename))
            _statistics[newName] = statsToRename;
        _statsCatalog?.Rename(oldName, newName);

        IncrementSchemaVersion();
    }

    public void AddColumn(string tableName, ColumnDefinition column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(column);

        var table = GetTable(tableName);

        if (table.Schema.TryGetColumnOrdinal(column.Name, out _))
            throw new InvalidOperationException($"Column '{column.Name}' already exists in table '{tableName}'.");

        if (!column.IsNullable)
        {
            // A NOT NULL column can only be added to an empty table; otherwise NULL would
            // appear in every existing row, violating the constraint.
            var cells = new BPlusTree(_pager, table.RootPageId).ReadAll();
            if (cells.Count > 0)
                throw new InvalidOperationException(
                    $"Cannot add NOT NULL column '{column.Name}' to '{tableName}' because it already contains rows. " +
                    "Add a nullable column instead.");
        }

        var newColumns = table.Schema.Columns.Append(column).ToArray();
        var newSchema = new TableSchema(tableName, newColumns, table.Schema.PrimaryKeyOrdinal);
        _catalog.Update(new TableInfo(table.TableId, tableName, table.RootPageId, newSchema));

        // Column set changed — existing stats are stale.
        InvalidateStats(tableName);

        IncrementSchemaVersion();
    }

    public void RenameColumn(string tableName, string oldColumnName, string newColumnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldColumnName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newColumnName);

        var table = GetTable(tableName);

        if (!table.Schema.TryGetColumnOrdinal(oldColumnName, out var ordinal))
            throw new InvalidOperationException($"Column '{oldColumnName}' does not exist in table '{tableName}'.");

        if (table.Schema.TryGetColumnOrdinal(newColumnName, out _))
            throw new InvalidOperationException($"Column '{newColumnName}' already exists in table '{tableName}'.");

        var newColumns = table.Schema.Columns
            .Select((c, i) => i == ordinal ? c with { Name = newColumnName } : c)
            .ToArray();
        var newSchema = new TableSchema(tableName, newColumns, table.Schema.PrimaryKeyOrdinal);
        _catalog.Update(new TableInfo(table.TableId, tableName, table.RootPageId, newSchema));

        // Update any index metadata that references the old column name.
        if (_indexCatalog is not null)
        {
            foreach (var index in _indexCatalog.GetByTable(tableName))
            {
                var updatedColumns = index.Columns
                    .Select(c => string.Equals(c, oldColumnName, StringComparison.OrdinalIgnoreCase) ? newColumnName : c)
                    .ToList();
                _indexCatalog.UpdateEntry(index with { Columns = updatedColumns });
            }
        }

        // Column name changed — NDV keys are stale.
        InvalidateStats(tableName);

        IncrementSchemaVersion();
    }

    public void Insert(string tableName, IReadOnlyList<object?> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var table = GetTable(tableName);
        var primaryKey = ExtractPrimaryKey(table.Schema, values);
        var payload = SerializeRow(table.Schema, values);

        try
        {
            new BPlusTree(_pager, table.RootPageId).Insert(new TableLeafCell(primaryKey, payload));
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Duplicate primary key"))
        {
            throw new InvalidOperationException(
                $"Table '{tableName}' already contains a row with primary key {primaryKey}.", ex);
        }

        // Maintain secondary indexes.
        var indexes = GetIndexesForTable(tableName);
        if (indexes.Count > 0)
        {
            var rowValues = values.ToArray();
            foreach (var index in indexes)
            {
                var columnOrdinals = index.Columns.Select(c => table.Schema.GetColumnOrdinal(c)).ToList();
                var tree = new SecondaryBPlusTree(_pager, index.RootPageId);
                InsertIndexEntry(tree, table.Schema.Columns, columnOrdinals, rowValues, primaryKey, index.IsUnique);
            }
        }
    }

    public void Delete(string tableName, long primaryKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var table = GetTable(tableName);

        // Remove index entries before deleting the row so we can still read its values.
        var indexes = GetIndexesForTable(tableName);
        if (indexes.Count > 0 && TryReadByPrimaryKey(tableName, primaryKey, out var existingValues) && existingValues is not null)
        {
            foreach (var index in indexes)
            {
                var columnOrdinals = index.Columns.Select(c => table.Schema.GetColumnOrdinal(c)).ToList();
                var tree = new SecondaryBPlusTree(_pager, index.RootPageId);
                DeleteIndexEntry(tree, table.Schema.Columns, columnOrdinals, existingValues, primaryKey, index.IsUnique);
            }
        }

        try
        {
            new BPlusTree(_pager, table.RootPageId).Delete(primaryKey);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            throw new InvalidOperationException(
                $"Table '{tableName}' does not contain a row with primary key {primaryKey}.", ex);
        }
    }

    public void Update(string tableName, long primaryKey, IReadOnlyList<object?> newValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var table = GetTable(tableName);
        var indexes = GetIndexesForTable(tableName);

        // Read old values before update so we can remove stale index entries.
        object?[]? oldValues = null;
        if (indexes.Count > 0)
            TryReadByPrimaryKey(tableName, primaryKey, out oldValues);

        var payload = SerializeRow(table.Schema, newValues);

        try
        {
            new BPlusTree(_pager, table.RootPageId).Update(new TableLeafCell(primaryKey, payload));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            throw new InvalidOperationException(
                $"Table '{tableName}' does not contain a row with primary key {primaryKey}.", ex);
        }

        // Maintain secondary indexes.
        if (indexes.Count > 0 && oldValues is not null)
        {
            var newValuesArray = newValues.ToArray();
            foreach (var index in indexes)
            {
                var columnOrdinals = index.Columns.Select(c => table.Schema.GetColumnOrdinal(c)).ToList();
                var tree = new SecondaryBPlusTree(_pager, index.RootPageId);
                DeleteIndexEntry(tree, table.Schema.Columns, columnOrdinals, oldValues, primaryKey, index.IsUnique);
                InsertIndexEntry(tree, table.Schema.Columns, columnOrdinals, newValuesArray, primaryKey, index.IsUnique);
            }
        }
    }

    public bool TryReadByPrimaryKey(string tableName, long primaryKey, out object?[]? values)
    {
        var table = GetTable(tableName);

        if (!new BPlusTree(_pager, table.RootPageId).TryGet(primaryKey, out var cell))
        {
            values = null;
            return false;
        }

        values = _rowSerializer.Read(table.Schema, cell.Payload);
        return true;
    }

    public IReadOnlyList<object?[]> ReadAll(string tableName)
    {
        var table = GetTable(tableName);
        var cells = new BPlusTree(_pager, table.RootPageId).ReadAll();
        var rows = new List<object?[]>(cells.Count);

        foreach (var cell in cells)
        {
            rows.Add(_rowSerializer.Read(table.Schema, cell.Payload));
        }

        return rows;
    }

    /// <summary>
    /// Returns primary keys of rows that satisfy <paramref name="range"/> on <paramref name="index"/>.
    /// </summary>
    public IReadOnlyList<long> SeekByIndex(IndexInfo index, IndexSeekRange range)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(range);

        var tree = new SecondaryBPlusTree(_pager, index.RootPageId);
        var entries = tree.RangeSeek(range);
        return entries.Select(e => e.PrimaryKey).ToList();
    }

    /// <summary>
    /// Scans all entries in <paramref name="index"/> in ascending key order and returns their primary keys.
    /// </summary>
    public IReadOnlyList<long> ScanIndexAllOrdered(IndexInfo index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var tree = new SecondaryBPlusTree(_pager, index.RootPageId);
        // An unbounded range (both bounds null) matches every entry.
        var range = new IndexSeekRange(lowerKey: null, lowerInclusive: true, upperKey: null, upperInclusive: true);
        var entries = tree.RangeSeek(range);
        return entries.Select(e => e.PrimaryKey).ToList();
    }

    /// <summary>Returns the largest primary key currently stored in <paramref name="tableName"/>, or
    /// <c>null</c> when the table is empty. Used to drive AUTOINCREMENT key assignment.</summary>
    public long? GetMaxPrimaryKey(string tableName)
    {
        var table = GetTable(tableName);
        var cells = new BPlusTree(_pager, table.RootPageId).ReadAll();
        return cells.Count == 0 ? null : cells[^1].PrimaryKey;
    }

    /// <summary>Deletes all rows in <paramref name="tableName"/> and releases their pages back to the free
    /// list, leaving the table structure intact.</summary>
    public void TruncateTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var table = GetTable(tableName);

        // Remove all secondary index entries by rebuilding each index as empty.
        var indexes = GetIndexesForTable(tableName);
        foreach (var index in indexes)
        {
            new SecondaryBPlusTree(_pager, index.RootPageId).ReleaseAllPages();
            // Re-allocate a fresh empty root page so the index still exists.
            var newIndexRoot = _pager.AllocatePage(PageType.IndexLeaf);
            _indexCatalog!.UpdateEntry(index with { RootPageId = newIndexRoot });
        }

        // Release all table data pages and allocate a fresh empty root.
        new BPlusTree(_pager, table.RootPageId).ReleaseAllPages();
        var newRoot = _pager.AllocatePage(PageType.TableLeaf);
        _catalog.Update(new TableInfo(table.TableId, table.TableName, newRoot, table.Schema));

        // All rows gone — any persisted stats are now badly misleading.
        InvalidateStats(tableName);

        IncrementSchemaVersion();
    }

    // ── View catalog ────────────────────────────────────────────────────────

    public ViewInfo CreateView(string viewName, string selectSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectSql);

        var viewCatalog = EnsureViewCatalog();
        var status = viewCatalog.TryInsert(viewName, selectSql, out var view);

        return status switch
        {
            ViewCatalogInsertStatus.DuplicateViewName => throw new InvalidOperationException($"View '{viewName}' already exists."),
            ViewCatalogInsertStatus.CatalogFull => throw new InvalidOperationException("The view catalog page is full."),
            _ => view!
        };
    }

    public bool TryGetView(string viewName, out ViewInfo? view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        if (_viewCatalog is null) { view = null; return false; }
        return _viewCatalog.TryGetByName(viewName, out view);
    }

    public IReadOnlyList<ViewInfo> GetAllViews() => _viewCatalog?.ReadAll() ?? [];

    public void DropView(string viewName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);

        if (_viewCatalog is null || !_viewCatalog.TryGetByName(viewName, out var view))
            throw new InvalidOperationException($"View '{viewName}' does not exist.");

        _viewCatalog.Delete(view!.ViewId);
        IncrementSchemaVersion();
    }

    public void Dispose()
    {
        if (_ownsPager)
        {
            _pager.Dispose();
        }
    }

    private uint EnsureCatalogRootPage()
    {
        var header = _pager.ReadDatabaseHeader();
        if (header.RootPageId != 0)
        {
            return header.RootPageId;
        }

        var catalogRootPageId = _pager.AllocatePage(PageType.TableLeaf);
        header = _pager.ReadDatabaseHeader();
        _pager.WriteDatabaseHeader(header with { RootPageId = catalogRootPageId });
        return catalogRootPageId;
    }

    private IndexCatalogStore EnsureIndexCatalog()
    {
        if (_indexCatalog is not null)
            return _indexCatalog;

        var rootPageId = _pager.AllocatePage(PageType.TableLeaf);
        var header = _pager.ReadDatabaseHeader();
        _pager.WriteDatabaseHeader(header with { IndexCatalogRootPageId = rootPageId });
        _indexCatalog = new IndexCatalogStore(_pager, rootPageId);
        return _indexCatalog;
    }

    private ViewCatalogStore EnsureViewCatalog()
    {
        if (_viewCatalog is not null)
            return _viewCatalog;

        var rootPageId = _pager.AllocatePage(PageType.TableLeaf);
        var header = _pager.ReadDatabaseHeader();
        _pager.WriteDatabaseHeader(header with { ViewCatalogRootPageId = rootPageId });
        _viewCatalog = new ViewCatalogStore(_pager, rootPageId);
        return _viewCatalog;
    }

    private StatsCatalogStore EnsureStatsCatalog()
    {
        if (_statsCatalog is not null)
            return _statsCatalog;

        var rootPageId = _pager.AllocatePage(PageType.TableLeaf);
        var header = _pager.ReadDatabaseHeader();
        // Bump format version to 4 so StatsCatalogRootPageId is round-tripped on re-open.
        _pager.WriteDatabaseHeader(header with
        {
            StatsCatalogRootPageId = rootPageId,
            FormatVersion = Math.Max(header.FormatVersion, DbConstants.FormatVersion)
        });
        _statsCatalog = new StatsCatalogStore(_pager, rootPageId);
        return _statsCatalog;
    }

    private static void InsertIndexEntry(
        SecondaryBPlusTree tree,
        IReadOnlyList<ColumnDefinition> columns,
        IReadOnlyList<int> columnOrdinals,
        object?[] rowValues,
        long primaryKey,
        bool isUnique)
    {
        var key = isUnique
            ? IndexKeyEncoder.Encode(columns, columnOrdinals, rowValues)
            : IndexKeyEncoder.Encode(columns, columnOrdinals, rowValues, primaryKey);

        tree.Insert(key, primaryKey, isUnique);
    }

    private static void DeleteIndexEntry(
        SecondaryBPlusTree tree,
        IReadOnlyList<ColumnDefinition> columns,
        IReadOnlyList<int> columnOrdinals,
        object?[] rowValues,
        long primaryKey,
        bool isUnique)
    {
        var key = isUnique
            ? IndexKeyEncoder.Encode(columns, columnOrdinals, rowValues)
            : IndexKeyEncoder.Encode(columns, columnOrdinals, rowValues, primaryKey);

        tree.Delete(key);
    }

    private byte[] SerializeRow(TableSchema schema, IReadOnlyList<object?> values)
    {
        var buffer = new byte[_rowSerializer.GetRequiredSize(schema, values)];
        var bytesWritten = _rowSerializer.Write(schema, values, buffer);
        return buffer.AsSpan(0, bytesWritten).ToArray();
    }

    private static long ExtractPrimaryKey(TableSchema schema, IReadOnlyList<object?> values)
    {
        if (values[schema.PrimaryKeyOrdinal] is not long primaryKey)
        {
            throw new InvalidOperationException($"The primary key column '{schema.PrimaryKeyColumn.Name}' requires an Int64 value.");
        }

        return primaryKey;
    }

    private void IncrementSchemaVersion()
    {
        var header = _pager.ReadDatabaseHeader();
        _pager.WriteDatabaseHeader(header with { SchemaVersion = header.SchemaVersion + 1 });
    }
}
