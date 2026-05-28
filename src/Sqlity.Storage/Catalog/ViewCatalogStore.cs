using System.Text;
using Sqlity.Storage.Abstractions;
using Sqlity.Storage.BTree;
using Sqlity.Storage.Pages;
using Sqlity.Storage.Rows;

namespace Sqlity.Storage.Catalog;

/// <summary>
/// Stores view definitions in a single catalog page, mirroring the pattern used by
/// <see cref="IndexCatalogStore"/> for index metadata.
/// </summary>
internal sealed class ViewCatalogStore
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    private static readonly TableSchema CatalogSchema =
        new(
            "__sqlity_views",
            new[]
            {
                new ColumnDefinition("view_id", ColumnType.Int64, IsNullable: false),
                new ColumnDefinition("view_name", ColumnType.String),
                new ColumnDefinition("select_sql", ColumnType.String)
            },
            primaryKeyOrdinal: 0);

    private readonly IPager _pager;
    private readonly uint _catalogRootPageId;
    private readonly RowSerializer _rowSerializer = new();

    public ViewCatalogStore(IPager pager, uint catalogRootPageId)
    {
        ArgumentNullException.ThrowIfNull(pager);
        _pager = pager;
        _catalogRootPageId = catalogRootPageId;
    }

    public IReadOnlyList<ViewInfo> ReadAll()
    {
        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var entries = new List<ViewInfo>(page.CellCount);

        foreach (var cell in page.ReadAllCells())
            entries.Add(ReadEntry(cell));

        return entries;
    }

    public bool TryGetByName(string viewName, out ViewInfo? view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);

        foreach (var entry in ReadAll())
        {
            if (string.Equals(entry.ViewName, viewName, StringComparison.OrdinalIgnoreCase))
            {
                view = entry;
                return true;
            }
        }

        view = null;
        return false;
    }

    public ViewCatalogInsertStatus TryInsert(string viewName, string selectSql, out ViewInfo? view)
    {
        if (TryGetByName(viewName, out _))
        {
            view = null;
            return ViewCatalogInsertStatus.DuplicateViewName;
        }

        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var viewId = GetNextViewId(page);

        var values = new object?[] { viewId, viewName, selectSql };
        var payload = SerializeValues(values);
        var insertStatus = page.TryInsert(new TableLeafCell(viewId, payload));

        if (insertStatus == TableLeafInsertStatus.PageFull)
        {
            view = null;
            return ViewCatalogInsertStatus.CatalogFull;
        }

        _pager.WritePage(page.Page);
        view = new ViewInfo(viewId, viewName, selectSql);
        return ViewCatalogInsertStatus.Success;
    }

    public void Delete(long viewId)
    {
        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var status = page.TryDelete(viewId);

        if (status == TableLeafDeleteStatus.NotFound)
            throw new InvalidOperationException($"View catalog entry with id {viewId} not found.");

        _pager.WritePage(page.Page);
    }

    private long GetNextViewId(TableLeafPage page)
    {
        var cells = page.ReadAllCells();
        return cells.Count == 0 ? 1 : cells[^1].PrimaryKey + 1;
    }

    private ViewInfo ReadEntry(TableLeafCell cell)
    {
        var values = _rowSerializer.Read(CatalogSchema, cell.Payload);
        var viewId = values[0] is long id ? id : cell.PrimaryKey;
        var viewName = values[1] as string ?? throw new InvalidDataException("View catalog row must have a view name.");
        var selectSql = values[2] as string ?? throw new InvalidDataException("View catalog row must have a SELECT SQL.");
        return new ViewInfo(viewId, viewName, selectSql);
    }

    private byte[] SerializeValues(object?[] values)
    {
        var buffer = new byte[_rowSerializer.GetRequiredSize(CatalogSchema, values)];
        var bytesWritten = _rowSerializer.Write(CatalogSchema, values, buffer);
        return buffer.AsSpan(0, bytesWritten).ToArray();
    }
}

internal enum ViewCatalogInsertStatus
{
    Success = 0,
    DuplicateViewName = 1,
    CatalogFull = 2
}
