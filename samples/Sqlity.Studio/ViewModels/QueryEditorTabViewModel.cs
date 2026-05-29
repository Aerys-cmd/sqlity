using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sqlity.Studio.Services;
using System.Diagnostics;

namespace Sqlity.Studio.ViewModels;

public sealed partial class QueryEditorTabViewModel : ObservableObject
{
    private readonly SqlitySession _session;
    private readonly Action _onSchemaChanged;

    [ObservableProperty] private string _header = "Query";
    [ObservableProperty] private string _sqlText = "";
    [ObservableProperty] private bool _isBusy;

    public ResultGridViewModel Result { get; } = new();

    public QueryEditorTabViewModel(SqlitySession session, Action onSchemaChanged, string initialSql = "")
    {
        _session = session;
        _onSchemaChanged = onSchemaChanged;
        _sqlText = initialSql;
    }

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        var sql = SqlText.Trim();
        if (string.IsNullOrEmpty(sql)) return;

        IsBusy = true;
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await _session.ExecuteAsync(sql);
            sw.Stop();
            Result.LoadResult(result, sw.ElapsedMilliseconds);
            _onSchemaChanged();
        }
        catch (OperationCanceledException)
        {
            Result.LoadError("Query was cancelled.");
        }
        catch (Exception ex)
        {
            Result.LoadError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecute() => !IsBusy;

    partial void OnIsBusyChanged(bool value) => ExecuteCommand.NotifyCanExecuteChanged();
}
