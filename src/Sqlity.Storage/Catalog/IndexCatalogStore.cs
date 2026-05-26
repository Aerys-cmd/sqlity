using Sqlity.Storage.Abstractions;
using Sqlity.Storage.BTree;
using Sqlity.Storage.Pages;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Catalog;

/// <summary>
/// Stores index metadata in a single leaf page, mirroring the pattern used by
/// <see cref="CatalogStore"/> for table metadata.
/// </summary>
internal sealed class IndexCatalogStore
{
    private static readonly TableSchema CatalogSchema =
        new(
            "__sqlity_indexes",
            new[]
            {
                new ColumnDefinition("index_id", ColumnType.Int64, IsNullable: false),
                new ColumnDefinition("index_name", ColumnType.String),
                new ColumnDefinition("table_name", ColumnType.String),
                new ColumnDefinition("root_page_id", ColumnType.Int64),
                new ColumnDefinition("metadata_blob", ColumnType.Blob)
            },
            primaryKeyOrdinal: 0);

    private readonly IPager _pager;
    private readonly uint _catalogRootPageId;
    private readonly RowSerializer _rowSerializer = new();
    private readonly IndexMetadataSerializer _metaSerializer = new();

    public IndexCatalogStore(IPager pager, uint catalogRootPageId)
    {
        ArgumentNullException.ThrowIfNull(pager);
        _pager = pager;
        _catalogRootPageId = catalogRootPageId;
    }

    public IReadOnlyList<IndexInfo> ReadAll()
    {
        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var entries = new List<IndexInfo>(page.CellCount);

        foreach (var cell in page.ReadAllCells())
            entries.Add(ReadEntry(cell));

        return entries;
    }

    public bool TryGetByName(string indexName, out IndexInfo? index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        foreach (var entry in ReadAll())
        {
            if (string.Equals(entry.IndexName, indexName, StringComparison.OrdinalIgnoreCase))
            {
                index = entry;
                return true;
            }
        }

        index = null;
        return false;
    }

    public IReadOnlyList<IndexInfo> GetByTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        return ReadAll()
            .Where(i => string.Equals(i.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public IndexCatalogInsertStatus TryInsert(
        string indexName,
        string tableName,
        uint rootPageId,
        IReadOnlyList<string> columns,
        bool isUnique,
        out IndexInfo? index)
    {
        if (TryGetByName(indexName, out _))
        {
            index = null;
            return IndexCatalogInsertStatus.DuplicateIndexName;
        }

        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var indexId = GetNextIndexId(page);
        var metaBlob = _metaSerializer.Serialize(columns, isUnique);

        var values = new object?[]
        {
            indexId,
            indexName,
            tableName,
            (long)rootPageId,
            metaBlob
        };

        var payload = SerializeValues(values);
        var insertStatus = page.TryInsert(new TableLeafCell(indexId, payload));

        if (insertStatus == TableLeafInsertStatus.PageFull)
        {
            index = null;
            return IndexCatalogInsertStatus.CatalogFull;
        }

        _pager.WritePage(page.Page);
        index = new IndexInfo(indexId, indexName, tableName, rootPageId, columns, isUnique);
        return IndexCatalogInsertStatus.Success;
    }

    private long GetNextIndexId(TableLeafPage page)
    {
        var cells = page.ReadAllCells();
        return cells.Count == 0 ? 1 : cells[^1].PrimaryKey + 1;
    }

    private IndexInfo ReadEntry(TableLeafCell cell)
    {
        var values = _rowSerializer.Read(CatalogSchema, cell.Payload);
        var indexId = values[0] is long id ? id : cell.PrimaryKey;
        var indexName = values[1] as string ?? throw new InvalidDataException("Index catalog row must have an index name.");
        var tableName = values[2] as string ?? throw new InvalidDataException("Index catalog row must have a table name.");
        var rootPageId = checked((uint)(values[3] as long? ?? throw new InvalidDataException("Index catalog row must have a root page id.")));
        var metaBlob = values[4] as byte[] ?? throw new InvalidDataException("Index catalog row must have a metadata blob.");
        var (columns, isUnique) = _metaSerializer.Deserialize(metaBlob);
        return new IndexInfo(indexId, indexName, tableName, rootPageId, columns, isUnique);
    }

    private byte[] SerializeValues(object?[] values)
    {
        var buffer = new byte[_rowSerializer.GetRequiredSize(CatalogSchema, values)];
        var bytesWritten = _rowSerializer.Write(CatalogSchema, values, buffer);
        return buffer.AsSpan(0, bytesWritten).ToArray();
    }
}

internal enum IndexCatalogInsertStatus
{
    Success = 0,
    DuplicateIndexName = 1,
    CatalogFull = 2
}
