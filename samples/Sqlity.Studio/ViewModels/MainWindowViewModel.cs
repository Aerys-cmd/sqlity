using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sqlity.Studio.Services;
using System.Collections.ObjectModel;

namespace Sqlity.Studio.ViewModels;

/// <summary>Shown in the "Open Recent" submenu — one item per recent path.</summary>
public sealed class RecentDatabaseViewModel
{
    public string Path { get; }
    public string DisplayName { get; }
    public IRelayCommand OpenCommand { get; }

    public RecentDatabaseViewModel(string path, MainWindowViewModel parent)
    {
        Path = path;
        DisplayName = System.IO.Path.GetFileName(path);
        OpenCommand = new RelayCommand(() => parent.OpenDatabase(path));
    }
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private SqlitySession? _session;
    private readonly AppSettings _settings;

    [ObservableProperty] private QueryEditorTabViewModel? _selectedTab;
    [ObservableProperty] private string _windowTitle = "Sqlity Studio";
    [ObservableProperty] private string _statusBarText = "No database open. Use File → Open Database.";
    [ObservableProperty] private bool _isDatabaseOpen;
    [ObservableProperty] private bool _isNoDatabaseOpen = true;
    [ObservableProperty] private bool _inTransaction;
    [ObservableProperty] private string _transactionStatus = "";

    public ObservableCollection<QueryEditorTabViewModel> Tabs { get; } = [];
    public SchemaExplorerViewModel SchemaExplorer { get; } = new();
    public SchemaViewerViewModel SchemaViewer { get; } = new();

    public IReadOnlyList<RecentDatabaseViewModel> RecentDatabases =>
        _settings.RecentDatabases
            .Where(File.Exists)
            .Select(p => new RecentDatabaseViewModel(p, this))
            .ToList();

    public MainWindowViewModel(AppSettings settings)
    {
        _settings = settings;
        SchemaExplorer.TableDoubleClicked += OnTableDoubleClicked;
        SchemaExplorer.TableSelected += OnTableSelected;

        if (!string.IsNullOrEmpty(settings.LastOpenedDatabase) &&
            File.Exists(settings.LastOpenedDatabase))
        {
            OpenDatabase(settings.LastOpenedDatabase);
        }
    }

    public async Task PickAndOpenDatabaseAsync(IStorageProvider storage)
    {
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Sqlity Database",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Sqlity Database") { Patterns = ["*.sqlity"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path is not null) OpenDatabase(path);
        }
    }

    public async Task PickAndCreateDatabaseAsync(IStorageProvider storage)
    {
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create Sqlity Database",
            SuggestedFileName = "new_database.sqlity",
            DefaultExtension = ".sqlity",
            FileTypeChoices =
            [
                new FilePickerFileType("Sqlity Database") { Patterns = ["*.sqlity"] }
            ]
        });

        if (file is not null)
        {
            var path = file.TryGetLocalPath();
            if (path is not null) OpenDatabase(path);
        }
    }

    public void OpenDatabase(string path)
    {
        try
        {
            var old = _session;
            _session = new SqlitySession(path);
            try { old?.Dispose(); } catch { }

            Tabs.Clear();
            SchemaViewer.Clear();

            _settings.AddRecent(path);
            OnPropertyChanged(nameof(RecentDatabases));

            IsDatabaseOpen = true;
            IsNoDatabaseOpen = false;
            WindowTitle = $"Sqlity Studio — {System.IO.Path.GetFileName(path)}";

            AddTab();
            RefreshSchema();
            RefreshStats();
        }
        catch (Exception ex)
        {
            StatusBarText = $"Error opening database: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddTab()
    {
        if (_session is null) return;
        var tab = new QueryEditorTabViewModel(_session, RefreshAll)
        {
            Header = $"Query {Tabs.Count + 1}"
        };
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(QueryEditorTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (SelectedTab == tab)
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Max(0, index - 1)] : null;
    }

    private void OnTableDoubleClicked(string tableName)
    {
        if (_session is null) return;

        string sql;
        try
        {
            var info = _session.GetTableInfo(tableName);
            var pkCol = info.Schema.PrimaryKeyColumn.Name;
            sql = $"SELECT * FROM \"{tableName}\" ORDER BY \"{pkCol}\" LIMIT 100";
        }
        catch
        {
            sql = $"SELECT * FROM \"{tableName}\" LIMIT 100";
        }

        var tab = new QueryEditorTabViewModel(_session, RefreshAll, sql)
        {
            Header = tableName
        };
        Tabs.Add(tab);
        SelectedTab = tab;

        // Auto-execute the browse query
        tab.ExecuteCommand.Execute(null);
    }

    private void OnTableSelected(string tableName)
    {
        if (_session is null) return;
        try { SchemaViewer.LoadTable(_session, tableName); }
        catch { /* schema view is best-effort */ }
    }

    private void RefreshSchema()
    {
        if (_session is null) return;
        try { SchemaExplorer.Refresh(_session); }
        catch { }
    }

    private void RefreshStats()
    {
        if (_session is null) return;
        try
        {
            var fi = new FileInfo(_session.FilePath);
            var fileSize = FormatFileSize(fi.Exists ? fi.Length : 0);
            var tables = _session.ListUserTableInfos().Count;
            var views = _session.ListViews().Count;
            var indexes = _session.ListAllIndexes().Count;

            InTransaction = _session.InTransaction;
            TransactionStatus = InTransaction ? "⚡ IN TRANSACTION" : "";

            StatusBarText =
                $"{_session.FilePath}  |  " +
                $"Tables: {tables}  Views: {views}  Indexes: {indexes}  |  {fileSize}";
        }
        catch { }
    }

    private void RefreshAll()
    {
        RefreshSchema();
        RefreshStats();
    }

    public void OnShutdown()
    {
        _session?.Dispose();
        _settings.Save();
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB"
    };
}
