using CommunityToolkit.Mvvm.ComponentModel;
using Sqlity.Studio.Services;
using System.Collections.ObjectModel;

namespace Sqlity.Studio.ViewModels;

public sealed partial class SchemaExplorerViewModel : ObservableObject
{
    [ObservableProperty] private SchemaNodeViewModel? _selectedNode;

    public ObservableCollection<SchemaNodeViewModel> Nodes { get; } = [];

    public event Action<string>? TableDoubleClicked;
    public event Action<string>? TableSelected;

    public void Refresh(SqlitySession? session)
    {
        Nodes.Clear();
        if (session is null) return;

        var tables = session.ListUserTableInfos();
        var views = session.ListViews();
        var indexes = session.ListAllIndexes();

        var tablesNode = new SchemaNodeViewModel
        {
            Name = $"Tables ({tables.Count})",
            NodeType = SchemaNodeType.Category
        };
        foreach (var t in tables)
            tablesNode.Children.Add(new SchemaNodeViewModel { Name = t.TableName, NodeType = SchemaNodeType.Table });

        var viewsNode = new SchemaNodeViewModel
        {
            Name = $"Views ({views.Count})",
            NodeType = SchemaNodeType.Category
        };
        foreach (var v in views)
            viewsNode.Children.Add(new SchemaNodeViewModel { Name = v.ViewName, NodeType = SchemaNodeType.View });

        var indexesNode = new SchemaNodeViewModel
        {
            Name = $"Indexes ({indexes.Count})",
            NodeType = SchemaNodeType.Category
        };
        foreach (var idx in indexes)
            indexesNode.Children.Add(new SchemaNodeViewModel
            {
                Name = idx.IndexName,
                NodeType = SchemaNodeType.Index,
                Extra = $"ON {idx.TableName} ({string.Join(", ", idx.Columns)}){(idx.IsUnique ? " UNIQUE" : "")}"
            });

        Nodes.Add(tablesNode);
        Nodes.Add(viewsNode);
        Nodes.Add(indexesNode);
    }

    public void OnNodeDoubleClicked(SchemaNodeViewModel node)
    {
        if (node.NodeType == SchemaNodeType.Table)
            TableDoubleClicked?.Invoke(node.Name);
    }

    public void OnNodeSelected(SchemaNodeViewModel? node)
    {
        SelectedNode = node;
        if (node?.NodeType == SchemaNodeType.Table)
            TableSelected?.Invoke(node.Name);
    }
}
