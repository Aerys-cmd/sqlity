using Sqlity.Storage.Abstractions;
using Sqlity.Storage.BTree;
using Sqlity.Storage.Pages;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Catalog;

internal sealed class CatalogStore
{
    private static readonly TableSchema CatalogSchema =
        new(
            "__sqlity_tables",
            new[]
            {
                new ColumnDefinition("table_id", ColumnType.Int64, IsNullable: false),
                new ColumnDefinition("table_name", ColumnType.String),
                new ColumnDefinition("root_page_id", ColumnType.Int64),
                new ColumnDefinition("schema_blob", ColumnType.Blob)
            },
            primaryKeyOrdinal: 0);

    private readonly IPager _pager;
    private readonly uint _catalogRootPageId;
    private readonly RowSerializer _rowSerializer = new();
    private readonly TableSchemaSerializer _schemaSerializer = new();

    public CatalogStore(IPager pager, uint catalogRootPageId)
    {
        ArgumentNullException.ThrowIfNull(pager);

        _pager = pager;
        _catalogRootPageId = catalogRootPageId;
    }

    public IReadOnlyList<TableInfo> ReadTables()
    {
        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var entries = new List<TableInfo>(page.CellCount);

        foreach (var cell in page.ReadAllCells())
        {
            entries.Add(ReadEntry(cell));
        }

        return entries;
    }

    public bool TryGetByName(string tableName, out TableInfo? table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        foreach (var entry in ReadTables())
        {
            if (string.Equals(entry.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            {
                table = entry;
                return true;
            }
        }

        table = null;
        return false;
    }

    public CatalogInsertStatus TryInsert(TableSchema schema, uint rootPageId, out TableInfo? table)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (TryGetByName(schema.TableName, out _))
        {
            table = null;
            return CatalogInsertStatus.DuplicateTableName;
        }

        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var tableId = GetNextTableId(page);
        var entry = new TableInfo(tableId, schema.TableName, rootPageId, schema);
        var payload = SerializeEntry(entry);
        var insertStatus = page.TryInsert(new TableLeafCell(tableId, payload));

        if (insertStatus == TableLeafInsertStatus.PageFull)
        {
            table = null;
            return CatalogInsertStatus.CatalogFull;
        }

        _pager.WritePage(page.Page);
        table = entry;
        return CatalogInsertStatus.Success;
    }

    private long GetNextTableId(TableLeafPage page)
    {
        var cells = page.ReadAllCells();
        return cells.Count == 0 ? 1 : cells[^1].PrimaryKey + 1;
    }

    private TableInfo ReadEntry(TableLeafCell cell)
    {
        var values = _rowSerializer.Read(CatalogSchema, cell.Payload);
        var tableId = values[0] is long storedTableId ? storedTableId : cell.PrimaryKey;
        var tableName = values[1] as string ?? throw new InvalidDataException("Catalog rows must persist a table name.");
        var rootPageId = checked((uint)(values[2] as long? ?? throw new InvalidDataException("Catalog rows must persist a root page id.")));
        var schemaBlob = values[3] as byte[] ?? throw new InvalidDataException("Catalog rows must persist a schema blob.");
        var schema = _schemaSerializer.Deserialize(schemaBlob);

        return new TableInfo(tableId, tableName, rootPageId, schema);
    }

    private byte[] SerializeEntry(TableInfo entry)
    {
        var schemaBlob = _schemaSerializer.Serialize(entry.Schema);
        var values = new object?[]
        {
            entry.TableId,
            entry.TableName,
            (long)entry.RootPageId,
            schemaBlob
        };

        var buffer = new byte[_rowSerializer.GetRequiredSize(CatalogSchema, values)];
        var bytesWritten = _rowSerializer.Write(CatalogSchema, values, buffer);
        return buffer.AsSpan(0, bytesWritten).ToArray();
    }
}

internal enum CatalogInsertStatus
{
    Success = 0,
    DuplicateTableName = 1,
    CatalogFull = 2
}
