using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Sqlity.Studio.Services;
using Sqlity.Studio.ViewModels;
using Sqlity.Studio.Views;
using System.Xml;

namespace Sqlity.Studio;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        RegisterSqlHighlighting();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            var vm = new MainWindowViewModel(settings);
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => vm.OnShutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RegisterSqlHighlighting()
    {
        try
        {
            using var stream = typeof(App).Assembly
                .GetManifestResourceStream("Sqlity.Studio.Assets.SQL.xshd");
            if (stream is null) return;

            using var reader = new XmlTextReader(stream);
            var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting("SQL", [".sql", ".sqlity"], def);
        }
        catch
        {
            // Highlighting is optional; continue without it.
        }
    }
}
