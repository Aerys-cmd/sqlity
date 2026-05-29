using Avalonia.Controls;
using Avalonia.Data;
using Sqlity.Studio.ViewModels;

namespace Sqlity.Studio.Views;

public partial class ResultGridView : UserControl
{
    private ResultGridViewModel? _vm;

    public ResultGridView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not ResultGridViewModel vm) return;
        _vm = vm;

        vm.PropertyChanged += (_, pe) =>
        {
            if (pe.PropertyName == nameof(ResultGridViewModel.Columns))
                RebuildColumns();
            else if (pe.PropertyName == nameof(ResultGridViewModel.Rows))
                ApplyRows();
        };
    }

    private void RebuildColumns()
    {
        var grid = this.FindControl<DataGrid>("ResultDataGrid")!;
        grid.Columns.Clear();
        if (_vm is null) return;

        for (int i = 0; i < _vm.Columns.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = _vm.Columns[i],
                Binding = new Binding($"[{i}]"),
                IsReadOnly = true,
                MinWidth = 60,
                MaxWidth = 400
            });
        }

        ApplyRows();
    }

    private void ApplyRows()
    {
        var grid = this.FindControl<DataGrid>("ResultDataGrid")!;
        grid.ItemsSource = _vm?.Rows;
    }
}
