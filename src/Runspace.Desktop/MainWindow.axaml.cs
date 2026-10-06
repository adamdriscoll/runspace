using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Runspace.Core;
using Runspace.Desktop.ViewModels;
using Runspace.PowerShell;

namespace Runspace.Desktop;

public partial class MainWindow : Window
{
    private readonly ConsoleViewModel _model = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ConsoleNode> _navigationHistory = [];
    private readonly Queue<string> _history = new();
    private IConsoleSession? _session;
    private ConsoleResult? _result;
    private ConsoleNode? _currentNode;
    private IReadOnlyList<ConsoleColumn> _columns = [];
    private IReadOnlyList<ConsoleRow> _rows = [];
    private DataGridCollectionView? _view;
    private CancellationTokenSource? _active;
    private NavigationItem? _drivesRoot;
    private long _generation;
    private int _historyPosition = -1;
    private bool _closing;
    private bool _readyToClose;
    private bool _changingRows;
    private bool _layoutReadFailed;
    private readonly bool _persistLayout;

    public MainWindow() : this(null, true) { }

    public MainWindow(IConsoleSession? session, bool persistLayout = false)
    {
        _session = session;
        _persistLayout = persistLayout;
        InitializeComponent();
        DataContext = _model;
        BuildNavigation();
        NavigationTree.AddHandler(TreeViewItem.ExpandedEvent, Expanded);
        Opened += async (_, _) => await SafeAsync(InitializeAsync);
        Closing += OnClosing;
        KeyDown += WindowKeyDown;
        UpdateNavigationButtons();
    }

    private static NavigationItem Folder(string id, string name) => new(new(id, name, ResourceKind.Overview, $"Browse {name.ToLowerInvariant()}."));

    private void BuildNavigation()
    {
        var local = Folder("local", "Local System");
        local.IsExpanded = true;
        var network = Folder("network", "Network Configuration");
        var accounts = Folder("accounts", "Local Users and Groups");
        foreach (var node in BuiltInCatalog.LocalSystem)
        {
            var item = new NavigationItem(node, node.Kind == ResourceKind.Registry);
            if (node.Kind is ResourceKind.NetworkInterfaces or ResourceKind.NetworkProperties)
                network.Children.Add(item);
            else if (node.Kind is ResourceKind.LocalUsers or ResourceKind.LocalGroups)
                accounts.Children.Add(item);
            else
                local.Children.Add(item);
        }
        local.Children.Insert(3, network);
        local.Children.Insert(7, accounts);
        _drivesRoot = Folder("provider-drives", "PowerShell Drives");
        _drivesRoot.IsExpanded = true;
        _model.Roots.Add(local);
        var networkRoot = Folder("network-root", "Network");
        networkRoot.Children.Add(new(new("managed-computers", "Managed Computers", ResourceKind.ManagedComputers,
            "Inspect this computer. Remote-computer management is not part of this foundation.")));
        _model.Roots.Add(networkRoot);
        _model.Roots.Add(_drivesRoot);
    }

    private async Task InitializeAsync()
    {
        try { if (_persistLayout) LoadLayout(); }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _layoutReadFailed = true;
            ReportError(exception);
        }
        _model.IsBusy = true;
        _session ??= await Task.Run<IConsoleSession>(() => new PowerShellSession(), _lifetime.Token);
        _model.Runtime = $"Local / PowerShell {_session.RuntimeVersion}";
        await RefreshDrivesAsync();
        _model.IsBusy = false;
        NavigationTree.SelectedItem = _model.Roots[0].Children.First(item => item.Node.Kind == ResourceKind.Processes);
    }

    private IConsoleSession Session => _session ?? throw new InvalidOperationException("PowerShell is not ready. Check Diagnostics for startup errors.");

    private async Task RefreshDrivesAsync()
    {
        var nodes = await Session.GetDriveNodesAsync(_lifetime.Token);
        if (_drivesRoot is null) return;
        _drivesRoot.Children.Clear();
        foreach (var node in nodes)
            _drivesRoot.Children.Add(new(node, true));
    }

    private async void NavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavigationTree.SelectedItem is NavigationItem item && item.Node.Id != "loading")
            await SafeAsync(() => NavigateAsync(item.Node));
    }

    private async void Expanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: NavigationItem { IsLazy: true, IsLoading: false } item })
            return;
        await SafeAsync(async () =>
        {
            item.IsLoading = true;
            try
            {
                var result = await Session.QueryAsync(item.Node, _lifetime.Token);
                try
                {
                    AppendDiagnostics(result);
                    if (result.Outcome is InvocationOutcome.Failed or InvocationOutcome.Cancelled)
                        throw new InvalidOperationException(result.Diagnostics.LastOrDefault()?.Message ?? "Unable to load this resource. Collapse and expand to retry.");
                    item.Children.Clear();
                    foreach (var row in result.Rows.Where(row => row.RelatedNode is not null))
                        item.Children.Add(new(row.RelatedNode!, true));
                    item.IsLazy = false;
                }
                finally { Session.ReleaseResult(result.Id); }
            }
            finally { item.IsLoading = false; }
        });
    }

    private async Task NavigateAsync(ConsoleNode node, bool addHistory = true)
    {
        if (_closing) return;
        var generation = ++_generation;
        _active?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _active = cancellation;
        _currentNode = node;
        _model.Title = node.Path is null ? $"Local System > {node.Name}" : node.Path;
        _model.Description = node.Description;
        LocationBox.Text = node.Kind == ResourceKind.ProviderPath ? node.Path : string.Empty;
        LocationBox.IsVisible = node.Kind == ResourceKind.ProviderPath;
        GoButton.IsVisible = LocationBox.IsVisible;
        Breadcrumb.IsVisible = !LocationBox.IsVisible;
        DocumentTabs.SelectedIndex = 0;
        FilterBox.Text = string.Empty;
        if (addHistory)
        {
            if (_historyPosition < _navigationHistory.Count - 1)
                _navigationHistory.RemoveRange(_historyPosition + 1, _navigationHistory.Count - _historyPosition - 1);
            if (_navigationHistory.Count == 0 || _navigationHistory[^1] != node)
                _navigationHistory.Add(node);
            _historyPosition = _navigationHistory.Count - 1;
        }
        UpdateNavigationButtons();
        _model.IsBusy = true;
        _model.Status = $"Loading {node.Name}...";
        UpdateActions();
        try
        {
            if (node.Kind == ResourceKind.Overview)
            {
                ShowOverview(node);
                return;
            }
            var result = await Session.QueryAsync(node, cancellation.Token);
            RecordInvocation(node.Name, result);
            if (_closing || generation != _generation)
            {
                Session.ReleaseResult(result.Id);
                return;
            }
            ReleaseCurrentResult();
            _result = result;
            Display(result.Columns, result.Rows);
            _model.Script = result.Script;
            _model.Runtime = $"Local / PowerShell {Session.RuntimeVersion}";
            var message = result.Outcome switch
            {
                InvocationOutcome.Failed => "Query failed. See Diagnostics; this is not an empty successful result.",
                InvocationOutcome.Cancelled => "Query cancelled. Displayed results may be incomplete.",
                InvocationOutcome.CompletedWithErrors => "Some objects could not be retrieved or evaluated. See Diagnostics.",
                _ when result.Rows.Count == 0 => "No objects were returned.",
                _ => string.Empty
            };
            ResultMessage.IsVisible = message.Length > 0;
            ResultMessageText.Text = message;
            _model.Status = $"{result.Outcome} - {node.Name} ({result.Duration.TotalSeconds:N2} s)";
        }
        finally
        {
            if (generation == _generation)
            {
                _active = null;
                _model.IsBusy = false;
                UpdateActions();
            }
        }
    }

    private void ShowOverview(ConsoleNode node)
    {
        ReleaseCurrentResult();
        var item = FindNode(_model.Roots, node.Id);
        var children = item?.Children ?? [];
        ConsoleColumn[] columns = [new("Name", "Resource", Width: 190), new("Description", "Description", Width: 400), new("Available", "Availability")];
        var rows = children.Select(child => new ConsoleRow(Guid.NewGuid(), new Dictionary<string, ConsoleCell>
        {
            ["Name"] = ConsoleCell.From(child.Name),
            ["Description"] = ConsoleCell.From(child.Node.Description),
            ["Available"] = ConsoleCell.From(child.Node.WindowsOnly && !OperatingSystem.IsWindows() ? "Windows only" : "Available")
        }, child.Node)).ToArray();
        Display(columns, rows);
        _model.Script = string.Empty;
        _model.Status = "Choose a resource to view its objects.";
        ResultMessage.IsVisible = false;
    }

    private static NavigationItem? FindNode(IEnumerable<NavigationItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Node.Id == id) return item;
            var child = FindNode(item.Children, id);
            if (child is not null) return child;
        }
        return null;
    }

    private void Display(IReadOnlyList<ConsoleColumn> columns, IReadOnlyList<ConsoleRow> rows)
    {
        _changingRows = true;
        try
        {
            _columns = columns;
            _rows = rows;
            ResultsGrid.SelectedItems.Clear();
            ResultsGrid.Columns.Clear();
            foreach (var column in columns)
            {
                ResultsGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = column.Header,
                    Tag = column.Key,
                    Width = new DataGridLength(column.Kind is ColumnKind.Number or ColumnKind.Bytes or ColumnKind.Boolean
                        ? Math.Min(column.Width, 110) : column.Width),
                    CanUserSort = true,
                    CustomSortComparer = ResultView.CreateComparer(column.Key),
                    CellTemplate = new FuncDataTemplate<ConsoleRow>((row, _) =>
                    {
                        var cell = row?.Cells.GetValueOrDefault(column.Key);
                        var text = new TextBlock
                        {
                            Text = cell?.Display ?? "(missing)", Margin = new Thickness(7, 0),
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextAlignment = column.Kind is ColumnKind.Number or ColumnKind.Bytes ? TextAlignment.Right : TextAlignment.Left
                        };
                        ToolTip.SetTip(text, cell?.Error ?? cell?.Display);
                        return text;
                    })
                });
            }
            _view = new DataGridCollectionView(rows) { Filter = MatchesFilter };
            ResultsGrid.ItemsSource = _view;
        }
        finally { _changingRows = false; }
        UpdateCounts();
        UpdateActions();
    }

    private bool MatchesFilter(object item) => item is ConsoleRow row &&
        (string.IsNullOrWhiteSpace(FilterBox.Text) || row.SearchText.Contains(FilterBox.Text, StringComparison.CurrentCultureIgnoreCase));

    private void FilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (_view is null) return;
        var hadSelection = ResultsGrid.SelectedItems.Count > 0;
        _changingRows = true;
        try
        {
            _view.Refresh();
            _view.MoveCurrentToPosition(-1);
            ResultsGrid.SelectedItems.Clear();
            ResultsGrid.SelectedItem = null;
        }
        finally { _changingRows = false; }
        UpdateCounts();
        UpdateActions();
        if (hadSelection) _model.Status = "Selection cleared because the displayed filter changed.";
    }

    private IReadOnlyList<ConsoleRow> SelectedRows() => ResultsGrid.SelectedItems.Cast<ConsoleRow>().ToArray();
    private IReadOnlyList<ConsoleRow> VisibleRows() => _view?.Cast<ConsoleRow>().ToArray() ?? [];
    private void UpdateCounts() => _model.Count = $"{_view?.Count ?? 0:N0} / {_rows.Count:N0} objects | {ResultsGrid.SelectedItems.Count:N0} selected";

    private void ResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changingRows) return;
        UpdateCounts();
        UpdateActions();
    }

    private IReadOnlyList<ConsoleAction> CurrentActions()
    {
        if (_currentNode is null) return [];
        return BuiltInCatalog.GetActions(_currentNode, SelectedRows())
            .Select(action => action with { IsEnabled = action.IsEnabled && !_model.IsBusy && (action.Id != ConsoleActionId.Properties || _result is not null) })
            .ToArray();
    }

    private void UpdateActions()
    {
        if (ActionsPanel is null) return;
        var selection = SelectedRows();
        _model.Selection = selection.Count switch
        {
            0 => "No objects selected",
            1 => $"Selected: {selection[0].Label}",
            _ => $"{selection.Count:N0} objects selected"
        };
        ActionsPanel.Children.Clear();
        foreach (var group in CurrentActions().GroupBy(action => action.Group).OrderBy(group => group.Key switch
        {
            "General" => 0,
            "Related information" => 2,
            "Export" => 3,
            _ => 1
        }))
        {
            ActionsPanel.Children.Add(new Border
            {
                Background = Brush.Parse("#F1F1F1"), Padding = new Thickness(10, 5), Margin = new Thickness(0, 4, 0, 1),
                Child = new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#365875") }
            });
            foreach (var action in group)
            {
                var button = new Button { Content = ActionContent(action), Tag = action.Id, IsEnabled = action.IsEnabled };
                Avalonia.Automation.AutomationProperties.SetName(button, action.Name);
                button.Classes.Add("action");
                ToolTip.SetTip(button, action.Description);
                button.Click += async (_, _) => await SafeAsync(() => InvokeActionAsync(action));
                ActionsPanel.Children.Add(button);
            }
        }
    }

    private static Control ActionContent(ConsoleAction action)
    {
        var stop = action.Id is ConsoleActionId.StopProcess or ConsoleActionId.StopService or ConsoleActionId.RemoveDrive or ConsoleActionId.RemoveItem;
        var start = action.Id is ConsoleActionId.StartProcess or ConsoleActionId.StartService;
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Width = 12, Height = 12, Stretch = Stretch.Uniform,
            Fill = stop ? Brush.Parse("#C52D29") : start ? Brush.Parse("#42A447") : Brush.Parse("#477CAA"),
            Data = Geometry.Parse(start ? "M2,1 L12,7 L2,13 Z" : stop ? "M2,2 L12,2 L12,12 L2,12 Z" : "M1,1 L10,1 L13,4 L13,13 L1,13 Z")
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = action.Name, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private async Task InvokeActionAsync(ConsoleAction action)
    {
        if (!action.IsEnabled || _closing) return;
        var rows = action.Id is ConsoleActionId.AddDrive or ConsoleActionId.StartProcess ? [] : SelectedRows();
        var resultId = _result?.Id ?? Guid.Empty;
        var node = _currentNode;
        switch (action.Id)
        {
            case ConsoleActionId.Browse when rows.Count == 1 && rows[0].RelatedNode is { } related:
                await NavigateAsync(related);
                return;
            case ConsoleActionId.Properties when rows.Count == 1:
                await Dialogs.PropertiesAsync(this, rows[0].Label, await Session.InspectAsync(resultId, rows[0].Handle, _lifetime.Token));
                return;
            case ConsoleActionId.ProcessModules or ConsoleActionId.ProcessThreads when rows.Count == 1:
                var processId = rows[0].Cells.GetValueOrDefault("Id")?.Value?.ToString()
                    ?? throw new InvalidOperationException("The selected object has no process identity.");
                var kind = action.Id == ConsoleActionId.ProcessModules ? ResourceKind.ProcessModules : ResourceKind.ProcessThreads;
                await NavigateAsync(new($"{kind}:{processId}", $"{rows[0].Label} - {action.Name}", kind, action.Description, processId));
                return;
            case ConsoleActionId.Copy:
                if (Clipboard is not { } clipboard) throw new NotSupportedException("The platform clipboard is unavailable.");
                await clipboard.SetTextAsync(TabularText(rows, "\t"));
                _model.Status = $"Copied {rows.Count} selected objects.";
                return;
            case ConsoleActionId.Export:
                await ExportAsync();
                return;
        }
        var context = $"Local session\n{action.Description}\n" + (rows.Count == 0 ? string.Empty : string.Join(", ", rows.Take(8).Select(row => row.Label)));
        IReadOnlyDictionary<string, string> parameters = new Dictionary<string, string>();
        if (action.Parameters is { Count: > 0 })
        {
            var providers = action.Id == ConsoleActionId.AddDrive ? await Session.GetProvidersAsync(_lifetime.Token) : null;
            var value = rows.Count == 1 ? rows[0].Cells.GetValueOrDefault("Value")?.Display : null;
            var response = await Dialogs.ParametersAsync(this, action, context, providers, value);
            if (response is null) return;
            parameters = response;
        }
        if (action.RequiresConfirmation && !await Dialogs.ConfirmAsync(this, action.Name,
                $"{context}\n\nExecute on {rows.Count} selected object(s)? This operation may change system state and requires your current account's permissions."))
            return;
        if (_result?.Id != resultId && resultId != Guid.Empty)
            throw new InvalidOperationException("The result changed while this action was being prepared. Select the objects again.");

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = _generation;
        _active = cancellation;
        _model.IsBusy = true;
        _model.Status = $"Running {action.Name}...";
        UpdateActions();
        try
        {
            var result = await Session.ExecuteAsync(action.Id, resultId, rows.Select(row => row.Handle).ToArray(), parameters, cancellation.Token);
            RecordInvocation(action.Name, result);
            Session.ReleaseResult(result.Id);
            _model.Status = $"{action.Name}: {result.Outcome}";
            if (result.Outcome is InvocationOutcome.Failed or InvocationOutcome.Cancelled)
            {
                ResultMessage.IsVisible = true;
                ResultMessageText.Text = $"{action.Name}: {result.Outcome}. See Diagnostics.";
                return;
            }
            if (action.Id is ConsoleActionId.AddDrive or ConsoleActionId.RemoveDrive)
                await RefreshDrivesAsync();
            if (node is not null && _currentNode == node)
                await NavigateAsync(node, false);
            if (result.Outcome == InvocationOutcome.CompletedWithErrors)
            {
                ResultMessage.IsVisible = true;
                ResultMessageText.Text = $"{action.Name} completed with errors. The refreshed table does not mean every operation succeeded.";
                _model.Status = $"{action.Name}: completed with errors. See Diagnostics.";
            }
        }
        finally
        {
            if (_generation == generation && _active == cancellation)
            {
                _active = null;
                _model.IsBusy = false;
                UpdateActions();
            }
        }
    }

    private void RecordInvocation(string name, ConsoleResult result)
    {
        AppendDiagnostics(result);
        _history.Enqueue($"# {DateTimeOffset.Now:g} | {name} | {result.Outcome} | {result.Duration.TotalSeconds:N2}s\n{result.Script}\n");
        var trimmed = false;
        while (_history.Count > 200) { _history.Dequeue(); trimmed = true; }
        _model.History = (trimmed ? "# History retains the latest 200 invocations.\n\n" : string.Empty) + string.Join("\n", _history.Reverse());
    }

    private void AppendDiagnostics(ConsoleResult result)
    {
        foreach (var record in result.Diagnostics)
            _model.Diagnostics += $"{record.Timestamp:T} [{record.Stream}] {record.Message}\n";
        if (result.Outcome is not InvocationOutcome.Completed)
            DiagnosticsPane.IsVisible = true;
    }

    private async Task ExportAsync()
    {
        if (_columns.Count == 0) throw new InvalidOperationException("Choose a resource before exporting a table.");
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export visible table", SuggestedFileName = $"{_currentNode?.Name ?? "results"}.csv",
            DefaultExtension = "csv", FileTypeChoices = [new FilePickerFileType("CSV table") { Patterns = ["*.csv"] }]
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteAsync(TabularText(VisibleRows(), ","));
        _model.Status = $"Exported {VisibleRows().Count:N0} visible objects to {file.Name}.";
    }

    private string TabularText(IReadOnlyList<ConsoleRow> rows, string separator)
    {
        var columns = ResultsGrid.Columns.Where(column => column.IsVisible).OrderBy(column => column.DisplayIndex).ToArray();
        string Escape(string value) => separator == "," ? $"\"{value.Replace("\"", "\"\"")}\"" : value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        var text = new StringBuilder();
        text.AppendLine(string.Join(separator, columns.Select(column => Escape(column.Header?.ToString() ?? string.Empty))));
        foreach (var row in rows)
            text.AppendLine(string.Join(separator, columns.Select(column => Escape(row.Cells.GetValueOrDefault(column.Tag?.ToString() ?? string.Empty)?.Display ?? "(missing)"))));
        return text.ToString();
    }

    private void ContextMenuOpening(object? sender, CancelEventArgs e)
    {
        var items = new List<MenuItem>();
        foreach (var action in CurrentActions())
        {
            var item = new MenuItem { Header = action.Name, IsEnabled = action.IsEnabled };
            item.Click += async (_, _) => await SafeAsync(() => InvokeActionAsync(action));
            items.Add(item);
        }
        ResultContextMenu.ItemsSource = items;
    }

    private void ResultPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ResultsGrid).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is ConsoleRow item && !ResultsGrid.SelectedItems.Contains(item))
        {
            ResultsGrid.SelectedItems.Clear();
            ResultsGrid.SelectedItem = item;
        }
    }

    private async void ResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>() is not null)
            await SafeAsync(DefaultActionAsync);
    }

    private Task DefaultActionAsync()
    {
        var rows = SelectedRows();
        var id = rows.Count == 1 && rows[0].RelatedNode is not null ? ConsoleActionId.Browse : ConsoleActionId.Properties;
        var action = CurrentActions().FirstOrDefault(action => action.Id == id);
        return action is null ? Task.CompletedTask : InvokeActionAsync(action);
    }

    private async void ResultKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SafeAsync(DefaultActionAsync); }
        else if (e.Key == Key.C && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            var action = CurrentActions().FirstOrDefault(action => action.Id == ConsoleActionId.Copy);
            if (action is not null) await SafeAsync(() => InvokeActionAsync(action));
        }
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { e.Handled = true; await SafeAsync(RefreshAsync); }
        else if (e.Key == Key.F && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        { e.Handled = true; FilterBox.Focus(); }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Left or Key.Right)
        { e.Handled = true; await SafeAsync(() => MoveHistoryAsync(e.Key == Key.Left ? -1 : 1)); }
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _historyPosition > 0;
        ForwardButton.IsEnabled = _historyPosition >= 0 && _historyPosition < _navigationHistory.Count - 1;
    }

    private Task MoveHistoryAsync(int offset)
    {
        var position = _historyPosition + offset;
        if (position < 0 || position >= _navigationHistory.Count) return Task.CompletedTask;
        _historyPosition = position;
        return NavigateAsync(_navigationHistory[position], false);
    }

    private Task RefreshAsync() => _currentNode is null ? Task.CompletedTask : NavigateAsync(_currentNode, false);
    private Task GoAsync()
    {
        var path = LocationBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Enter a PowerShell provider path first.");
        return NavigateAsync(new($"path:{path}", path, ResourceKind.ProviderPath, "Browse the active session's provider location.", path));
    }

    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { if (!_closing) _model.Status = "Cancelled."; }
        catch (Exception exception)
        {
            ReportError(exception);
            _model.IsBusy = false;
            UpdateActions();
        }
    }

    private void ReportError(Exception exception)
    {
        Trace.TraceError(exception.ToString());
        _model.Status = $"Error: {exception.Message}";
        _model.Diagnostics += $"{DateTimeOffset.Now:T} [Console error] {exception}\n";
        DiagnosticsPane.IsVisible = true;
        ResultMessage.IsVisible = true;
        ResultMessageText.Text = exception.Message;
    }

    private void ReleaseCurrentResult()
    {
        if (_result is not null) _session?.ReleaseResult(_result.Id);
        _result = null;
    }

    private static string LayoutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Runspace", "layout.json");
    private sealed record Layout(double Width, double Height, double Left, double Right);

    private void LoadLayout()
    {
        if (!File.Exists(LayoutPath)) return;
        var layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(LayoutPath)) ?? throw new InvalidDataException("The saved layout is empty. Remove layout.json to reset it.");
        if (!double.IsFinite(layout.Width) || !double.IsFinite(layout.Height) || !double.IsFinite(layout.Left) || !double.IsFinite(layout.Right))
            throw new InvalidDataException("The saved layout contains invalid dimensions.");
        Width = Math.Clamp(layout.Width, 900, 2000);
        Height = Math.Clamp(layout.Height, 600, 1400);
        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(layout.Left, 160, 350));
        WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(Math.Clamp(layout.Right, 180, 350));
    }

    private void SaveLayout()
    {
        if (!_persistLayout || _layoutReadFailed) return;
        var directory = Path.GetDirectoryName(LayoutPath)!;
        Directory.CreateDirectory(directory);
        var temporary = LayoutPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Layout(Width, Height,
            WorkspaceGrid.ColumnDefinitions[0].ActualWidth, WorkspaceGrid.ColumnDefinitions[4].ActualWidth)));
        File.Move(temporary, LayoutPath, true);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_readyToClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _lifetime.Cancel();
        _active?.Cancel();
        await SafeAsync(() => { SaveLayout(); return Task.CompletedTask; });
        await SafeAsync(async () =>
        {
            ReleaseCurrentResult();
            if (_session is not null) await _session.DisposeAsync();
        });
        _lifetime.Dispose();
        _readyToClose = true;
        Close();
    }

    private async void RefreshClick(object? sender, RoutedEventArgs e) => await SafeAsync(RefreshAsync);
    private void StopClick(object? sender, RoutedEventArgs e) { _active?.Cancel(); _model.Status = "Stopping..."; }
    private async void BackClick(object? sender, RoutedEventArgs e) => await SafeAsync(() => MoveHistoryAsync(-1));
    private async void ForwardClick(object? sender, RoutedEventArgs e) => await SafeAsync(() => MoveHistoryAsync(1));
    private async void GoClick(object? sender, RoutedEventArgs e) => await SafeAsync(GoAsync);
    private async void LocationKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await SafeAsync(GoAsync); } }
    private async void PropertiesClick(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var action = CurrentActions().FirstOrDefault(action => action.Id == ConsoleActionId.Properties);
        if (action is { IsEnabled: true }) await InvokeActionAsync(action);
        else _model.Status = "Select one object to inspect its properties.";
    });
    private async void ExportClick(object? sender, RoutedEventArgs e) => await SafeAsync(ExportAsync);
    private void ResultsClick(object? sender, RoutedEventArgs e) => DocumentTabs.SelectedIndex = 0;
    private void HistoryClick(object? sender, RoutedEventArgs e) => DocumentTabs.SelectedIndex = 1;
    private void DiagnosticsClick(object? sender, RoutedEventArgs e) => DiagnosticsPane.IsVisible = !DiagnosticsPane.IsVisible;
    private void HideDiagnosticsClick(object? sender, RoutedEventArgs e) => DiagnosticsPane.IsVisible = false;
    private void ExitClick(object? sender, RoutedEventArgs e) => Close();
    private void ResetLayoutClick(object? sender, RoutedEventArgs e)
    {
        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(220);
        WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(220);
        DiagnosticsPane.IsVisible = false;
        _layoutReadFailed = false;
    }
    private async void ProvidersClick(object? sender, RoutedEventArgs e) => await SafeAsync(() => NavigateAsync(BuiltInCatalog.LocalSystem.First(node => node.Kind == ResourceKind.Providers)));
    private async void GoToPathClick(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var action = new ConsoleAction(ConsoleActionId.Browse, "Go to provider path", "Navigation", "Browse a literal provider location.", true,
            Parameters: [new("Path", "Provider path", DefaultValue: "Env:")]);
        var parameters = await Dialogs.ParametersAsync(this, action, action.Description);
        if (parameters is null) return;
        LocationBox.Text = parameters["Path"];
        await GoAsync();
    });
    private async void AddDriveClick(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        if (_model.IsBusy) { _model.Status = "Wait for the active operation before adding a drive."; return; }
        var drives = BuiltInCatalog.LocalSystem.First(node => node.Kind == ResourceKind.Drives);
        if (_currentNode?.Kind != ResourceKind.Drives)
            await NavigateAsync(drives);
        if (_result?.Outcome == InvocationOutcome.Completed)
            await InvokeActionAsync(BuiltInCatalog.GetActions(drives, []).First(action => action.Id == ConsoleActionId.AddDrive));
    });
    private async void AboutClick(object? sender, RoutedEventArgs e) => await SafeAsync(() => Dialogs.MessageAsync(this, "About Runspace",
        $"Runspace Administration Console\n.NET 10 / Avalonia 12 / PowerShell {Session.RuntimeVersion}\n\nAn original, cross-platform console inspired by PowerGUI. Built-in administration only; no PowerPacks, installer, or copied PowerGUI code.\n\nWindows-only views require Windows. Operations use your current account's permissions."));
    private async void ColumnsClick(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var window = new Window { Title = "Visible columns", Width = 300, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        foreach (var column in ResultsGrid.Columns)
        {
            var check = new CheckBox { Content = column.Header, IsChecked = column.IsVisible };
            check.IsCheckedChanged += (_, _) => column.IsVisible = check.IsChecked == true;
            panel.Children.Add(check);
        }
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();
        panel.Children.Add(close);
        window.Content = panel;
        await window.ShowDialog(this);
    });
}
