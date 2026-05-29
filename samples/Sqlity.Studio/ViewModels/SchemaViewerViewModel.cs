using CommunityToolkit.Mvvm.ComponentModel;
using Sqlity.Storage.Catalog;
using Sqlity.Storage.Rows;
using Sqlity.Studio.Services;
using System.Text;

namespace Sqlity.Studio.ViewModels;

public sealed record ColumnDisplayInfo(
    string Name,
    string Type,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsAutoIncrement,
    string? Default);

public sealed record IndexDisplayInfo(
    string Name,
    string Columns,
    bool IsUnique);

public sealed partial class SchemaViewerViewModel : ObservableObject
{
    [ObservableProperty] private string _tableName = "";
    [ObservableProperty] private string _ddlScript = "";
    [ObservableProperty] private IReadOnlyList<ColumnDisplayInfo> _columns = [];
    [ObservableProperty] private IReadOnlyList<IndexDisplayInfo> _indexes = [];
    [ObservableProperty] private bool _hasContent;
    [ObservableProperty] private bool _hasIndexes;

    public void LoadTable(SqlitySession session, string tableName)
    {
        var info = session.GetTableInfo(tableName);
        TableName = tableName;
        DdlScript = GenerateDdl(info);

        Columns = info.Schema.Columns
            .Select((col, i) => new ColumnDisplayInfo(
                col.Name,
                col.Type.ToString().ToUpperInvariant(),
                col.IsNullable,
                i == info.Schema.PrimaryKeyOrdinal,
                col.IsAutoIncrement,
                col.HasDefault ? FormatDefault(col.DefaultValue) : null))
            .ToList();

        var idxList = session.GetIndexesForTable(tableName)
            .Select(idx => new IndexDisplayInfo(
                idx.IndexName,
                string.Join(", ", idx.Columns),
                idx.IsUnique))
            .ToList();

        Indexes = idxList;
        HasIndexes = idxList.Count > 0;
        HasContent = true;
    }

    public void Clear()
    {
        TableName = "";
        DdlScript = "";
        Columns = [];
        Indexes = [];
        HasIndexes = false;
        HasContent = false;
    }

    private static string GenerateDdl(TableInfo table)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- Generated DDL (reconstructed from schema)");
        sb.AppendLine($"CREATE TABLE {table.TableName} (");
        for (var i = 0; i < table.Schema.Columns.Count; i++)
        {
            var col = table.Schema.Columns[i];
            var isPk = i == table.Schema.PrimaryKeyOrdinal;
            var typeName = col.Type switch
            {
                ColumnType.Int64 => "INTEGER",
                ColumnType.String => "TEXT",
                ColumnType.Float64 => "REAL",
                ColumnType.Boolean => "BOOLEAN",
                ColumnType.Blob => "BLOB",
                ColumnType.Date => "DATE",
                ColumnType.DateTime => "DATETIME",
                _ => "TEXT"
            };
            var parts = new List<string> { $"    {col.Name} {typeName}" };
            if (isPk) parts.Add("PRIMARY KEY");
            if (col.IsAutoIncrement) parts.Add("AUTOINCREMENT");
            if (!col.IsNullable && !isPk) parts.Add("NOT NULL");
            if (col.HasDefault) parts.Add($"DEFAULT {FormatDefault(col.DefaultValue)}");
            var comma = i < table.Schema.Columns.Count - 1 ? "," : "";
            sb.AppendLine(string.Join(" ", parts) + comma);
        }
        sb.Append(");");
        return sb.ToString();
    }

    private static string FormatDefault(object? val) => val switch
    {
        null => "NULL",
        string s => $"'{s}'",
        bool b => b ? "TRUE" : "FALSE",
        _ => val.ToString() ?? "NULL"
    };
}
