using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Sqlity.Studio.ViewModels;

namespace Sqlity.Studio.Views;

public partial class QueryEditorTabView : UserControl
{
    private QueryEditorTabViewModel? _vm;

    public QueryEditorTabView()
    {
        InitializeComponent();
        // Auto-focus the editor so the user can type immediately
        Loaded += (_, _) => this.FindControl<TextEditor>("SqlEditor")?.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not QueryEditorTabViewModel vm) return;
        if (_vm == vm) return; // Prevent duplicate wiring on DataContext re-set
        _vm = vm;

        var editor = this.FindControl<TextEditor>("SqlEditor");
        if (editor is null) return;

        // Apply SQL syntax highlighting
        var sqlDef = HighlightingManager.Instance.GetDefinition("SQL");
        if (sqlDef is not null)
            editor.SyntaxHighlighting = sqlDef;

        // Set initial text BEFORE subscribing to Document.Changed to avoid a spurious sync
        editor.Text = vm.SqlText;

        // Editor → ViewModel: keep SqlText in sync as user types (one-way, no round-trip)
        editor.Document.Changed += (_, _) => vm.SqlText = editor.Text;

        // Ctrl+Enter to execute
        editor.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter && ke.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                vm.ExecuteCommand.Execute(null);
                ke.Handled = true;
            }
        };
    }
}
