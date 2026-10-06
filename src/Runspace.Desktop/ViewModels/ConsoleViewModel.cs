using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Runspace.Core;

namespace Runspace.Desktop.ViewModels;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class NavigationItem : ObservableModel
{
    private bool _isExpanded;
    public NavigationItem(ConsoleNode node, bool lazy = false)
    {
        Node = node;
        IsLazy = lazy;
        if (lazy)
            Children.Add(new(new("loading", "Expand to load...", ResourceKind.Overview, string.Empty)));
    }

    public ConsoleNode Node { get; }
    public string Name => Node.Name;
    public string Description => Node.WindowsOnly && !OperatingSystem.IsWindows()
        ? $"{Node.Description} Available on Windows only." : Node.Description;
    public bool IsLazy { get; set; }
    public bool IsLoading { get; set; }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public ObservableCollection<NavigationItem> Children { get; } = [];
    public IBrush IconBrush => Node.Kind == ResourceKind.Overview ? Brushes.Goldenrod : Brush.Parse("#376DA0");
    public Geometry Icon => Geometry.Parse(Node.Kind == ResourceKind.Overview
        ? "M1,3 L6,3 L8,5 L14,5 L14,13 L1,13 Z"
        : Node.Kind is ResourceKind.Drives or ResourceKind.ProviderPath
            ? "M1,4 L14,4 L14,12 L1,12 Z M3,9 L5,9"
            : "M2,1 L11,1 L14,4 L14,14 L2,14 Z");
}

public sealed class ConsoleViewModel : ObservableModel
{
    private string _title = "Local System";
    private string _description = "Choose a resource in the navigation tree.";
    private string _status = "Starting PowerShell...";
    private string _runtime = "Local session";
    private string _count = "No objects";
    private string _selection = "No objects selected";
    private string _filter = string.Empty;
    private string _diagnostics = string.Empty;
    private string _history = string.Empty;
    private string _script = string.Empty;
    private bool _busy;
    public ObservableCollection<NavigationItem> Roots { get; } = [];
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Runtime { get => _runtime; set => Set(ref _runtime, value); }
    public string Count { get => _count; set => Set(ref _count, value); }
    public string Selection { get => _selection; set => Set(ref _selection, value); }
    public string Filter { get => _filter; set => Set(ref _filter, value); }
    public string Diagnostics { get => _diagnostics; set => Set(ref _diagnostics, value); }
    public string History { get => _history; set => Set(ref _history, value); }
    public string Script { get => _script; set => Set(ref _script, value); }
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
}
