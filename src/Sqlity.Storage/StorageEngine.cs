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
    }

    public static StorageEngine Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var pager = new FilePager(filePath);
        if (new FileInfo(filePath).Length == 0)
        {
            pager.InitializeNew();
        }

        return new StorageEngine(pager, ownsPager: true);
    }

    public IReadOnlyList<TableInfo> ListTables() => _catalog.ReadTables();

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

    public TableInfo GetTable(string tableName)
    {
        if (!_catalog.TryGetByName(tableName, out var table))
        {
            throw new InvalidOperationException($"Table '{tableName}' does not exist.");
        }

        return table!;
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
    }

    public void Delete(string tableName, long primaryKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var table = GetTable(tableName);

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
