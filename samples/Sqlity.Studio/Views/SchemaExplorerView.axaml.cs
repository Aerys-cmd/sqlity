using Avalonia.Controls;
using Avalonia.Input;
using Sqlity.Studio.ViewModels;

namespace Sqlity.Studio.Views;

public partial class SchemaExplorerView : UserControl
{
    public SchemaExplorerView() => InitializeComponent();

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SchemaExplorerViewModel vm) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is SchemaNodeViewModel node)
            vm.OnNodeSelected(node);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SchemaExplorerViewModel vm) return;
        if (vm.SelectedNode is { NodeType: SchemaNodeType.Table } node)
            vm.OnNodeDoubleClicked(node);
    }
}
