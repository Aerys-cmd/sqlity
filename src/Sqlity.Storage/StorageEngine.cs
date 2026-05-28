using Sqlity.Storage.Abstractions;
using Sqlity.Storage.BTree;
using Sqlity.Storage.Catalog;
using Sqlity.Storage.IO;
using Sqlity.Storage.Pages;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage;

public sealed class StorageEngine : IDisposable
{
    private readonly IPager _pager;
    private readonly bool _ownsPager;
    private readonly CatalogStore _catalog;
    private IndexCatalogStore? _indexCatalog;
    private readonly RowSerializer _rowSerializer = new();

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

        var indexRootPageId = _pager.ReadDatabaseHeader().IndexCatalogRootPageId;
        if (indexRootPageId != 0)
            _indexCatalog = new IndexCatalogStore(_pager, indexRootPageId);
    }

    public static StorageEngine Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var pager = new FilePager(filePath);
        if (new FileInfo(filePath).Length == 0)
        {
            pager.InitializeNew();
        }
        else
        {
            pager.RecoverIfNeeded();
        }

        return new StorageEngine(pager, ownsPager: true);
    }

    public IReadOnlyList<TableInfo> ListTables() => _catalog.ReadTables();

    public IReadOnlyList<IndexInfo> ListIndexes() => _indexCatalog?.ReadAll() ?? [];

    public IReadOnlyList<IndexInfo> GetIndexesForTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return _indexCatalog?.GetByTable(tableName) ?? [];
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
