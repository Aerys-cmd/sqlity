using CommunityToolkit.Mvvm.ComponentModel;
using Sqlity.Query;
using System.Globalization;

namespace Sqlity.Studio.ViewModels;

public sealed partial class ResultGridViewModel : ObservableObject
{
    [ObservableProperty] private IReadOnlyList<string> _columns = [];
    [ObservableProperty] private IReadOnlyList<string?[]> _rows = [];
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _rowCountText = "";

    public void LoadResult(QueryExecutionResult result, long elapsedMs)
    {
        HasError = false;
        ErrorText = "";
        Columns = result.Columns.ToList();
        Rows = result.Rows.Select(r => r.Select(FormatValue).ToArray()).ToList();
        HasResults = result.Columns.Count > 0;
        IsEmpty = false;
        RowCountText = HasResults ? $"{Rows.Count} row(s)" : $"{result.RowsAffected} row(s) affected";
        StatusText = HasResults
            ? $"{Rows.Count} row(s) returned in {elapsedMs} ms"
            : $"{result.RowsAffected} row(s) affected in {elapsedMs} ms";
    }

    public void LoadError(string message)
    {
        HasError = true;
        ErrorText = message;
        HasResults = false;
        IsEmpty = false;
        Columns = [];
        Rows = [];
        RowCountText = "";
        StatusText = "";
    }

    public void Clear()
    {
        HasError = false;
        ErrorText = "";
        HasResults = false;
        IsEmpty = true;
        Columns = [];
        Rows = [];
        RowCountText = "";
        StatusText = "";
    }

    private static string? FormatValue(object? val) => val switch
    {
        null => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        byte[] blob => $"X'{Convert.ToHexString(blob)}'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => val.ToString()
    };
}
