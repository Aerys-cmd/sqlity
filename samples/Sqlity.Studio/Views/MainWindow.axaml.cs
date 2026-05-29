using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sqlity.Studio.ViewModels;

namespace Sqlity.Studio.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private async void OnOpenDatabase_Click(object? sender, RoutedEventArgs e)
        => await Vm.PickAndOpenDatabaseAsync(StorageProvider);

    private async void OnCreateDatabase_Click(object? sender, RoutedEventArgs e)
        => await Vm.PickAndCreateDatabaseAsync(StorageProvider);

    private void OnExit_Click(object? sender, RoutedEventArgs e)
    {
        Vm.OnShutdown();
        Close();
    }

    private void OnCloseTabButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is QueryEditorTabViewModel tab)
            Vm.CloseTabCommand.Execute(tab);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Vm.OnShutdown();
        base.OnClosing(e);
    }
}
