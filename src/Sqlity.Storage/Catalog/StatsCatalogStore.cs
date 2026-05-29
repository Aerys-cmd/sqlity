using Sqlity.Storage.Abstractions;
using Sqlity.Storage.BTree;
using Sqlity.Storage.Pages;
using Sqlity.Storage.Rows;
using Sqlity.Storage.Statistics;

namespace Sqlity.Storage.Catalog;

/// <summary>
/// Persists query-planner statistics in a single leaf page (<c>__sqlity_stat1</c>),
/// mirroring the design of <see cref="IndexCatalogStore"/>.
/// </summary>
/// <remarks>
/// One row per analyzed table. The <c>stat_blob</c> column carries the per-column
/// NDV (number-of-distinct-values) payload encoded by <see cref="StatsMetadataSerializer"/>.
/// </remarks>
internal sealed class StatsCatalogStore
{
    private static readonly TableSchema CatalogSchema =
        new(
            "__sqlity_stat1",
            new[]
            {
                new ColumnDefinition("stat_id",    ColumnType.Int64,  IsNullable: false),
                new ColumnDefinition("table_name", ColumnType.String, IsNullable: false),
                new ColumnDefinition("row_count",  ColumnType.Int64,  IsNullable: false),
                new ColumnDefinition("stat_blob",  ColumnType.Blob,   IsNullable: false)
            },
            primaryKeyOrdinal: 0);

    private readonly IPager _pager;
    private readonly uint _catalogRootPageId;
    private readonly RowSerializer _rowSerializer = new();
    private readonly StatsMetadataSerializer _metaSerializer = new();

    public StatsCatalogStore(IPager pager, uint catalogRootPageId)
    {
        ArgumentNullException.ThrowIfNull(pager);
        _pager = pager;
        _catalogRootPageId = catalogRootPageId;
    }

    /// <summary>Returns all persisted statistics as a list of (tableName, stats) pairs.</summary>
    public IReadOnlyList<(string TableName, TableStatistics Stats)> ReadAll()
    {
        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var results = new List<(string, TableStatistics)>(page.CellCount);
        foreach (var cell in page.ReadAllCells())
        {
            if (TryReadEntry(cell, out var name, out var stats))
                results.Add((name!, stats!));
        }
        return results;
    }

    /// <summary>Returns stats for <paramref name="tableName"/>, or <see langword="null"/> if not found.</summary>
    public TableStatistics? TryGetByTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        foreach (var (name, stats) in ReadAll())
        {
            if (string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase))
                return stats;
        }
        return null;
    }

    /// <summary>
    /// Inserts or replaces the statistics row for <paramref name="tableName"/>.
    /// Returns <see langword="false"/> and leaves the catalog unchanged if the page is full.
    /// </summary>
    public bool Upsert(string tableName, TableStatistics stats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(stats);

        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));

        // Look for an existing entry so we can update in-place.
        var existing = FindEntry(page, tableName);
        if (existing.HasValue)
        {
            var payload = BuildPayload(existing.Value.StatId, tableName, stats);
            var status = page.TryUpdate(new TableLeafCell(existing.Value.StatId, payload));
            if (status == TableLeafUpdateStatus.NotFound)
                throw new InvalidOperationException($"Stats catalog: entry for '{tableName}' vanished during update.");
            if (status == TableLeafUpdateStatus.InsufficientSpace)
                return false; // best-effort: caller may continue without persistence
            _pager.WritePage(page.Page);
            return true;
        }

        // No existing entry — insert a new one.
        var newId = GetNextStatId(page);
        var newPayload = BuildPayload(newId, tableName, stats);
        var insertStatus = page.TryInsert(new TableLeafCell(newId, newPayload));
        if (insertStatus == TableLeafInsertStatus.PageFull)
            return false; // best-effort: continue without persistence
        _pager.WritePage(page.Page);
        return true;
    }

    /// <summary>Removes the statistics row for <paramref name="tableName"/> if it exists.</summary>
    public void Delete(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var existing = FindEntry(page, tableName);
        if (existing is null)
            return; // nothing to delete

        page.TryDelete(existing.Value.StatId); // ignore NotFound — already checked
        _pager.WritePage(page.Page);
    }

    /// <summary>
    /// Renames the statistics row from <paramref name="oldName"/> to <paramref name="newName"/>.
    /// No-op if no entry exists for <paramref name="oldName"/>.
    /// </summary>
    public void Rename(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var page = new TableLeafPage(_pager.ReadPage(_catalogRootPageId));
        var existing = FindEntry(page, oldName);
        if (existing is null)
            return; // no stats to rename

        var payload = BuildPayload(existing.Value.StatId, newName, existing.Value.Stats);
        var status = page.TryUpdate(new TableLeafCell(existing.Value.StatId, payload));
        if (status == TableLeafUpdateStatus.NotFound)
            throw new InvalidOperationException($"Stats catalog: entry for '{oldName}' vanished during rename.");
        // InsufficientSpace is unlikely (same size row), but if it happens, drop the entry.
        if (status == TableLeafUpdateStatus.InsufficientSpace)
        {
            page.TryDelete(existing.Value.StatId);
        }
        _pager.WritePage(page.Page);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private (long StatId, TableStatistics Stats)? FindEntry(TableLeafPage page, string tableName)
    {
        foreach (var cell in page.ReadAllCells())
        {
            if (TryReadEntry(cell, out var name, out var stats)
                && string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase))
                return (cell.PrimaryKey, stats!);
        }
        return null;
    }

    private bool TryReadEntry(TableLeafCell cell, out string? tableName, out TableStatistics? stats)
    {
        try
        {
            var values = _rowSerializer.Read(CatalogSchema, cell.Payload);
            tableName = values[1] as string ?? throw new InvalidDataException("Stats catalog row must have a table_name.");
            var rowCount = values[2] as long? ?? throw new InvalidDataException("Stats catalog row must have a row_count.");
            var blob = values[3] as byte[] ?? throw new InvalidDataException("Stats catalog row must have a stat_blob.");
            var columnNdv = _metaSerializer.Deserialize(blob);
            stats = new TableStatistics(rowCount, columnNdv);
            return true;
        }
        catch (Exception)
        {
            // Corrupt or unrecognised stats row — skip it rather than crashing DB open.
            tableName = null;
            stats = null;
            return false;
        }
    }

    private byte[] BuildPayload(long statId, string tableName, TableStatistics stats)
    {
        var blob = _metaSerializer.Serialize(stats.ColumnNdv);
        var values = new object?[] { statId, tableName, stats.RowCount, blob };
        var buffer = new byte[_rowSerializer.GetRequiredSize(CatalogSchema, values)];
        var written = _rowSerializer.Write(CatalogSchema, values, buffer);
        return buffer.AsSpan(0, written).ToArray();
    }

    private static long GetNextStatId(TableLeafPage page)
    {
        var cells = page.ReadAllCells();
        return cells.Count == 0 ? 1 : cells[^1].PrimaryKey + 1;
    }
}
