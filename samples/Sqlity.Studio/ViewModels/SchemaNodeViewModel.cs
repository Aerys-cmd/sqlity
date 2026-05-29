using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Sqlity.Studio.ViewModels;

public enum SchemaNodeType { Category, Table, View, Index }

public sealed partial class SchemaNodeViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded = true;

    public required string Name { get; init; }
    public required SchemaNodeType NodeType { get; init; }
    public string? Extra { get; init; }

    public ObservableCollection<SchemaNodeViewModel> Children { get; } = [];
}
