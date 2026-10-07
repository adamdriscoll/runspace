using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Runspace.Core;
using Runspace.Desktop.ViewModels;
using Runspace.PowerShell;

namespace Runspace.Desktop;

public partial class MainWindow : Window
{
    private const string UnresponsiveStatus = "Stopping / unresponsive. A native call may still be active; hard termination is not guaranteed.";
    private readonly ConsoleViewModel _model = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ConsoleNode> _navigationHistory = [];
    private readonly Queue<string> _history = new();
    private readonly DiagnosticBuffer _diagnosticBuffer = new();
    private bool _historyEvicted;
    private int _navigationEvictions;
    private IConsoleSession? _session;
    private ConsoleResult? _result;
    private ConsoleNode? _currentNode;
    private IReadOnlyList<ConsoleColumn> _columns = [];
    private IReadOnlyList<ConsoleRow> _rows = [];
    private DataGridCollectionView? _view;
    private string _appliedFilter = string.Empty;
    private CancellationTokenSource? _active;
    private Guid? _promptInvocationId;
    private NavigationItem? _drivesRoot;
    private (NavigationItem Item, bool Visible, string? Message, string Status)? _navigationWarning;
    private long _generation;
    private int _historyPosition = -1;
    private bool _closing;
    private bool _confirmingClose;
    private bool _readyToClose;
    private bool _changingRows;
    internal bool IsSessionReady { get; private set; }
    internal int ResultDisplayCount { get; private set; }

    public MainWindow() : this(null, true) { }

    public MainWindow(IConsoleSession? session, bool persistLayout = false, WorkspaceStore? workspaceStore = null)
    {
        _session = session;
        _workspaceStore = workspaceStore ?? (persistLayout ? new WorkspaceStore(new FileWorkspaceStorage(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Runspace"))) : null);
        InitializeComponent();
        DataContext = _model;
        BuildNavigation();
        NavigationTree.AddHandler(TreeViewItem.ExpandedEvent, Expanded);
        NavigationTree.AddHandler(TreeViewItem.CollapsedEvent, Collapsed);
        ResultsGrid.AddHandler(KeyDownEvent, ResultKeyDown, RoutingStrategies.Tunnel);
        ResultsGrid.AddHandler(KeyUpEvent, ResultKeyUp, RoutingStrategies.Tunnel);
        Opened += async (_, _) => await SafeAsync(InitializeAsync);
        Closing += OnClosing;
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _normalSize = new Size(Width, Height);
        };
        PositionChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) _normalPosition = Position;
        };
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
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
        _drivesRoot.IsLazy = true;
        _drivesRoot.Children.Add(new(new("loading", "Expand to discover session drives...", ResourceKind.Overview, string.Empty)));
        _model.Roots.Add(local);
        var networkRoot = Folder("network-root", "Network");
        networkRoot.Children.Add(new(new("managed-computers", "Managed Computers", ResourceKind.ManagedComputers,
            "Inspect this computer. Remote-computer management is not part of this foundation.")));
        _model.Roots.Add(networkRoot);
        _model.Roots.Add(_drivesRoot);
    }

    private async Task InitializeAsync()
    {
        LoadWorkspace();
        _model.IsBusy = true;
        _session ??= await Task.Run<IConsoleSession>(() => new PowerShellSession(), _lifetime.Token);
        if (_session is IInvocationHostSession interactive)
        {
            interactive.StateChanged += InvocationStateChanged;
        }
        _model.Runtime = $"Local / PowerShell {_session.RuntimeVersion}";
        _model.IsBusy = false;
        IsSessionReady = true;
        _model.Status = "Ready. Choose a resource or explicitly open the saved resource; no query has run.";
        UpdateWorkspaceNotice();
    }

    private IConsoleSession Session => _session ?? throw new InvalidOperationException("PowerShell is not ready. Check Diagnostics for startup errors.");

    private void BindHostPrompt(long generation, CancellationToken operationToken)
    {
        if (Session is IInvocationHostSession interactive)
            interactive.PromptHandler = async (prompt, token) =>
                await Dispatcher.UIThread.InvokeAsync(() => PresentHostPromptAsync(prompt, token, generation, operationToken));
    }

    private async Task<HostResponse?> PresentHostPromptAsync(HostPrompt prompt, CancellationToken token,
        long generation, CancellationToken operationToken)
    {
        if (_closing || generation != _generation || operationToken.IsCancellationRequested || token.IsCancellationRequested) return null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, operationToken, _lifetime.Token);
        var wasBusy = _model.IsBusy;
        _promptInvocationId = prompt.InvocationId;
        _model.IsBusy = true;
        _model.Status = "Awaiting PowerShell input...";
        UpdateActions();
        try { return await Dialogs.HostPromptAsync(this, prompt, cancellation.Token); }
        finally
        {
            if (_promptInvocationId == prompt.InvocationId) _promptInvocationId = null;
            if (!_closing && generation == _generation && !wasBusy && _active is null)
            {
                _model.IsBusy = false;
                UpdateActions();
            }
        }
    }

    private void InvocationStateChanged(InvocationStatus status)
    {
        var active = _active;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || !_model.IsBusy || _active != active) return;
            // Prompt presentation owns its status; queued or superseded invocations cannot claim the current view.
            if (status.InvocationId != _promptInvocationId) return;
            if (status.State == InvocationState.Stopping) _model.Status = "Stopping...";
            else if (status.State == InvocationState.Unresponsive)
                _model.Status = UnresponsiveStatus;
        });
    }

    private async Task RefreshDrivesAsync()
    {
        BindHostPrompt(_generation, _active?.Token ?? _lifetime.Token);
        var nodes = await Session.GetDriveNodesAsync(_lifetime.Token);
        if (_drivesRoot is null) return;
        _drivesRoot.Children.Clear();
        var capacity = NavigationCapacity();
        foreach (var node in nodes.Take(capacity))
            _drivesRoot.Children.Add(new(node, true));
        _drivesRoot.IsLazy = false;
        if (nodes.Count > capacity)
            NavigationLimit(_drivesRoot);
    }

    private async void NavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavigationTree.SelectedItem is NavigationItem item && item.Node.Id != "loading")
            await SafeAsync(() => NavigateAsync(item.Node));
    }

    private async void Expanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: NavigationItem { IsLazy: true, IsLoading: false } item } container)
            return;
        await SafeAsync(async () =>
        {
            item.IsLoading = true;
            try
            {
                if (!IsSessionReady) throw new InvalidOperationException("Wait for PowerShell to initialize before expanding resources.");
                if (item == _drivesRoot)
                {
                    await RefreshDrivesAsync();
                    return;
                }
                var generation = _generation;
                BindHostPrompt(_generation, _lifetime.Token);
                var result = await Session.QueryAsync(item.Node, _lifetime.Token);
                try
                {
                    AppendDiagnostics(result);
                    if (_closing || !container.IsExpanded) return;
                    if (result.Outcome is InvocationOutcome.Failed or InvocationOutcome.Cancelled)
                        throw new InvalidOperationException(result.Diagnostics.LastOrDefault()?.Message ?? "Unable to load this resource. Collapse and expand to retry.");
                    item.Children.Clear();
                    var candidates = result.Rows.Where(row => row.RelatedNode is not null).ToArray();
                    var capacity = NavigationCapacity();
                    foreach (var row in candidates.Take(capacity))
                        item.Children.Add(new(row.RelatedNode!, true));
                    var limited = candidates.Length > capacity || result.RetentionNotice is not null;
                    if (limited) NavigationLimit(item);
                    item.IsLazy = result.Outcome != InvocationOutcome.Completed || limited;
                    if (item.IsLazy)
                    {
                        if (item.Children.Count == 0)
                            item.Children.Add(new(new("loading", "Expand to retry...", ResourceKind.Overview, string.Empty)));
                        if (generation == _generation && !_model.IsBusy)
                        {
                            if (_navigationWarning?.Item != item)
                                _navigationWarning = (item, ResultMessage.IsVisible, ResultMessageText.Text, _model.Status);
                            _model.Status = $"{item.Name}: {result.Outcome}; navigation incomplete.";
                            ResultMessage.IsVisible = true;
                            ResultMessageText.Text = $"Navigation for {item.Name} is incomplete. Collapse and expand to retry; see Diagnostics." + ErrorSummary(result);
                            DiagnosticsPane.IsVisible = true;
                        }
                    }
                    else if (_navigationWarning is { } warning && warning.Item == item)
                    {
                        ResultMessage.IsVisible = warning.Visible;
                        ResultMessageText.Text = warning.Message;
                        _model.Status = warning.Status;
                        _navigationWarning = null;
                    }
                }
                finally { Session.ReleaseResult(result.Id); }
            }
            finally { item.IsLoading = false; }
        });
    }

    private static int NavigationCount(IEnumerable<NavigationItem> items) =>
        items.Sum(item => 1 + NavigationCount(item.Children.Where(child => child.Node.Id != "loading")));

    private int NavigationCapacity() => Math.Min(RetentionPolicy.NavigationChildren,
        Math.Max(0, RetentionPolicy.NavigationNodes - NavigationCount(_model.Roots)));

    private void NavigationLimit(NavigationItem item)
    {
        item.Children.Add(new(new("loading", "Navigation limited; use Location or Refresh", ResourceKind.Overview, string.Empty)));
        _diagnosticBuffer.Add(new(DateTimeOffset.Now, "Retention",
            $"Navigation for {item.Name} is limited to {RetentionPolicy.NavigationChildren} children / " +
            $"{RetentionPolicy.NavigationNodes:N0} loaded nodes. Collapse a branch to unload it; use a literal Location " +
            "to browse omitted paths or export the retained result table."));
        UpdateDiagnosticsText();
        DiagnosticsPane.IsVisible = true;
    }

    private static void Collapsed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: NavigationItem item } ||
            item.Node.Kind is not (ResourceKind.ProviderPath or ResourceKind.Registry)) return;
        item.Children.Clear();
        item.Children.Add(new(new("loading", "Expand to load...", ResourceKind.Overview, string.Empty)));
        item.IsLazy = true;
    }

    private void AddNavigationRoute(ConsoleNode node)
    {
        if (_historyPosition < _navigationHistory.Count - 1)
            _navigationHistory.RemoveRange(_historyPosition + 1, _navigationHistory.Count - _historyPosition - 1);
        if (_navigationHistory.Count == 0 || _navigationHistory[^1] != node)
            _navigationHistory.Add(node);
        if (_navigationHistory.Count > RetentionPolicy.NavigationRoutes)
        {
            _navigationHistory.RemoveAt(0);
            _navigationEvictions++;
        }
        _historyPosition = _navigationHistory.Count - 1;
    }

    private async Task NavigateAsync(ConsoleNode node, bool addHistory = true)
    {
        if (_closing) return;
        CaptureViewPreferences();
        _navigationWarning = null;
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
        if (addHistory)
            AddNavigationRoute(node);
        UpdateNavigationButtons();
        _model.IsBusy = true;
        _model.Status = $"Loading {node.Name}; query/display properties pending...";
        ResultMessage.IsVisible = true;
        ResultMessageText.Text = "Query and display-property evaluation pending. Previous objects, if any, remain visible until the result is ready.";
        UpdateActions();
        try
        {
            if (node.Kind == ResourceKind.Overview)
            {
                ShowOverview(node);
                return;
            }
            BindHostPrompt(generation, cancellation.Token);
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
                InvocationOutcome.Failed => "Query failed. Retained objects are incomplete; this is not an empty successful result. See Diagnostics.",
                InvocationOutcome.Cancelled => "Query cancelled. Displayed results may be incomplete.",
                InvocationOutcome.CompletedWithErrors => "Some objects could not be retrieved or evaluated. See Diagnostics.",
                _ when result.OutputSuppressed => "Output withheld because this invocation used credentials/secure input. See Diagnostics.",
                _ when result.Rows.Count == 0 => "No objects were returned.",
                _ => string.Empty
            };
            if (result.Outcome is InvocationOutcome.Failed or InvocationOutcome.CompletedWithErrors)
                message += ErrorSummary(result);
            if (result.RetentionNotice is { } notice) message += "\n" + notice;
            ResultMessage.IsVisible = message.Length > 0;
            ResultMessageText.Text = message;
            _model.Status = $"{result.Outcome} - {node.Name} ({result.Duration.TotalSeconds:N2} s)" +
                (_navigationEvictions == 0 ? string.Empty : $" | {_navigationEvictions} older Back routes evicted; requery using Location.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closing && generation == _generation)
            {
                _model.Status = "Query cancelled.";
                ResultMessage.IsVisible = true;
                ResultMessageText.Text = "Query cancelled. Previous objects, if any, remain visible; no new result was accepted.";
            }
        }
        catch (Exception exception)
        {
            ReportError(exception, updateView: !_closing && generation == _generation);
        }
        finally
        {
            if (!_closing && generation == _generation)
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
        ResultDisplayCount++;
        _changingRows = true;
        try
        {
            _columns = columns;
            _rows = rows;
            _displayedReference = StableReference(_currentNode);
            _view = null;
            _appliedFilter = string.Empty;
            FilterBox.Text = string.Empty;
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
            ApplyViewPreferences();
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
        var filter = FilterBox.Text ?? string.Empty;
        if (filter == _appliedFilter) return;
        _appliedFilter = filter;
        var selection = SelectedRows();
        var retained = selection.Where(MatchesFilter).ToArray();
        _changingRows = true;
        try
        {
            _view.Refresh();
            _view.MoveCurrentToPosition(-1);
            ResultsGrid.SelectedItems.Clear();
            ResultsGrid.SelectedItem = null;
            foreach (var row in retained) ResultsGrid.SelectedItems.Add(row);
        }
        finally { _changingRows = false; }
        UpdateCounts();
        UpdateActions();
        var removed = selection.Count - retained.Length;
        if (removed > 0 && !_model.IsBusy)
            _model.Status = $"Filter hid {removed:N0} selected object(s); hidden selection cleared.";
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
            var header = new Border
            {
                Padding = new Thickness(10, 5), Margin = new Thickness(0, 4, 0, 1),
                Child = new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold }
            };
            header.Bind(Border.BackgroundProperty, this.GetResourceObservable("ConsoleChrome"));
            ActionsPanel.Children.Add(header);
            foreach (var action in group)
            {
                var button = new Button { Content = ActionContent(action), Tag = action.Id, IsEnabled = action.IsEnabled };
                AutomationProperties.SetName(button, action.Name);
                AutomationProperties.SetHelpText(button, action.Description + (action.IsEnabled ? string.Empty :
                    " Unavailable for the current selection, resource, platform, or pending operation."));
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
            Data = Geometry.Parse(start ? "M2,1 L12,7 L2,13 Z" : stop ? "M2,2 L12,2 L12,12 L2,12 Z" : "M1,1 L10,1 L13,4 L13,13 L1,13 Z")
        };
        icon.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, icon.GetResourceObservable("ConsoleAccent"));
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        panel.Children.Add(icon);
        var label = new TextBlock { Text = action.Name, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 1);
        panel.Children.Add(label);
        return panel;
    }

    internal async Task InvokeActionAsync(ConsoleAction action)
    {
        if (!action.IsEnabled || _closing || _model.IsBusy) return;
        _navigationWarning = null;
        var rows = action.Id is ConsoleActionId.AddDrive or ConsoleActionId.StartProcess ? [] : SelectedRows();
        var resultId = _result?.Id ?? Guid.Empty;
        var node = _currentNode;
        switch (action.Id)
        {
            case ConsoleActionId.Browse when rows.Count == 1 && rows[0].RelatedNode is { } related:
                await NavigateAsync(related);
                return;
            case ConsoleActionId.Properties when rows.Count == 1:
                await InspectSelectionAsync(resultId, rows[0]);
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
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = _generation;
        _active = cancellation;
        _model.IsBusy = true;
        _model.Status = $"Running {action.Name}...";
        UpdateActions();
        try
        {
            var context = $"Local session\n{action.Description}\n" + (rows.Count == 0 ? string.Empty : string.Join(", ", rows.Take(8).Select(row => row.Label)));
            var promptAction = action.SupportsShouldProcess ? action with
            {
                Parameters = [.. action.Parameters ?? [], new("WhatIf", "Preview only (PowerShell WhatIf)", DefaultValue: "False", Choices: ["False", "True"])]
            } : action;
            var parameters = new Dictionary<string, string>();
            if (promptAction.Parameters is { Count: > 0 })
            {
                _model.Status = $"Awaiting parameters for {action.Name}...";
                BindHostPrompt(generation, cancellation.Token);
                var providers = action.Id == ConsoleActionId.AddDrive ? await Session.GetProvidersAsync(cancellation.Token) : null;
                var value = rows.Count == 1 ? rows[0].Cells.GetValueOrDefault("Value")?.Display : null;
                var response = await Dialogs.ParametersAsync(this, promptAction, context, providers, value, cancellation.Token);
                if (response is null)
                {
                    if (!_closing && generation == _generation) _model.Status = $"{action.Name}: Cancelled";
                    return;
                }
                parameters = new Dictionary<string, string>(response);
            }
            if (action.RequiresConfirmation)
            {
                if (action.SupportsShouldProcess)
                    parameters["Confirm"] = "True";
                else if (!await Dialogs.ConfirmAsync(this, action.Name,
                    $"{context}\n\nExecute on {rows.Count} fixed object(s)? This operation may change system state.", cancellation.Token))
                {
                    if (!_closing && generation == _generation) _model.Status = $"{action.Name}: Cancelled";
                    return;
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (_result?.Id != resultId && resultId != Guid.Empty)
                throw new InvalidOperationException("The result changed while this action was being prepared. Select the objects again.");
            _model.Status = $"Running {action.Name}...";
            BindHostPrompt(generation, cancellation.Token);
            var result = await Session.ExecuteAsync(action.Id, resultId, rows.Select(row => row.Handle).ToArray(), parameters, cancellation.Token);
            RecordInvocation(action.Name, result);
            var retained = false;
            try
            {
                if (!_closing && generation == _generation)
                {
                    switch (action.ResultPolicy)
                    {
                        case ActionResultPolicy.Refresh:
                            if (action.Id is ConsoleActionId.AddDrive or ConsoleActionId.RemoveDrive)
                                await RefreshDrivesAsync();
                            if (_closing || generation != _generation) return;
                            if (node is not null && _currentNode == node)
                            {
                                var refreshGeneration = _generation + 1;
                                await NavigateAsync(node, false);
                                if (_closing || refreshGeneration != _generation) return;
                            }
                            break;
                        case ActionResultPolicy.Replace:
                        case ActionResultPolicy.Related:
                            CaptureViewPreferences();
                            if (action.ResultPolicy == ActionResultPolicy.Related && rows.Count == 1)
                            {
                                var kind = action.Id == ConsoleActionId.ProcessModules ? ResourceKind.ProcessModules : ResourceKind.ProcessThreads;
                                var related = new ConsoleNode($"{kind}:{rows[0].Handle}", $"{rows[0].Label} - {action.Name}", kind,
                                    action.Description, rows[0].Cells.GetValueOrDefault("Id")?.Value?.ToString());
                                AddNavigationRoute(related);
                                _currentNode = related;
                                _model.Title = related.Name;
                                _model.Description = related.Description;
                                UpdateNavigationButtons();
                            }
                            ReleaseCurrentResult();
                            _result = result;
                            retained = true;
                            Display(result.Columns, result.Rows);
                            _model.Script = result.Script;
                            break;
                        case ActionResultPolicy.Retain:
                            break;
                    }
                    _model.Status = $"{action.Name}: {result.Outcome}";
                    if (result.Outcome != InvocationOutcome.Completed || result.OutputSuppressed || result.RetentionNotice is not null)
                    {
                        ResultMessage.IsVisible = true;
                        ResultMessageText.Text = result.OutputSuppressed
                            ? $"{action.Name}: {result.Outcome}. Output withheld because this invocation used credentials/secure input; see Diagnostics."
                            : $"{action.Name}: {result.Outcome}. Partial operations/results may remain; see Diagnostics." + ErrorSummary(result);
                        if (result.RetentionNotice is { } notice) ResultMessageText.Text += "\n" + notice;
                    }
                }
            }
            finally { if (!retained) Session.ReleaseResult(result.Id); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closing && generation == _generation) _model.Status = $"{action.Name}: Cancelled";
        }
        catch (Exception exception) when (_closing || generation != _generation)
        {
            ReportError(exception, updateView: false);
        }
        finally
        {
            if (!_closing && _generation == generation && _active == cancellation)
            {
                _active = null;
                _model.IsBusy = false;
                UpdateActions();
            }
        }
    }

    private async Task InspectSelectionAsync(Guid resultId, ConsoleRow row)
    {
        var generation = _generation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _active = cancellation;
        _model.IsBusy = true;
        _model.Status = $"Inspecting {row.Label} properties (pending)...";
        UpdateActions();
        try
        {
            var properties = await Session.InspectAsync(resultId, row.Handle, cancellation.Token);
            if (_closing || generation != _generation) return;
            cancellation.Token.ThrowIfCancellationRequested();
            _model.Status = $"Inspected {row.Label}.";
            await Dialogs.PropertiesAsync(this, row.Label, properties);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closing && generation == _generation) _model.Status = "Inspection cancelled.";
        }
        catch (Exception exception) when (_closing || generation != _generation)
        {
            ReportError(exception, updateView: false);
        }
        finally
        {
            if (!_closing && generation == _generation && _active == cancellation)
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
        _history.Enqueue($"# {DateTimeOffset.Now:g} | {name} | {result.Outcome} | {result.Duration.TotalSeconds:N2}s\n" +
            $"{RetentionPolicy.BoundText(result.Script)}\n{result.RetentionNotice}\n");
        while (_history.Count > RetentionPolicy.HistoryEntries) { _history.Dequeue(); _historyEvicted = true; }
        _model.History = (_historyEvicted ? $"# History retains the latest {RetentionPolicy.HistoryEntries} invocations; older descriptions evicted. Save/copy before leaving.\n\n" : string.Empty) +
            string.Join("\n", _history.Reverse());
    }

    private void AppendDiagnostics(ConsoleResult result)
    {
        foreach (var record in result.Diagnostics)
            _diagnosticBuffer.Add(record);
        if (result.RetentionNotice is { } notice)
            _diagnosticBuffer.Add(new(DateTimeOffset.Now, "Retention", notice));
        UpdateDiagnosticsText();
        if (result.Outcome is not InvocationOutcome.Completed || result.OutputSuppressed ||
            result.RetentionNotice is not null || result.Diagnostics.Any(record => record.Stream == "Retention"))
            DiagnosticsPane.IsVisible = true;
    }

    private void UpdateDiagnosticsText() => _model.Diagnostics = string.Join("\n",
        _diagnosticBuffer.Snapshot().Select(record => $"{record.Timestamp:T} [{record.Stream}] {record.Message}"));

    private static string ErrorSummary(ConsoleResult result)
    {
        var count = result.Diagnostics.Count(record => record.Stream == "Error");
        return $" ({count:N0} error record{(count == 1 ? string.Empty : "s")})";
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
        ResultContextMenu.Placement = PlacementMode.Pointer;
        PopulateContextMenu();
    }

    private void PopulateContextMenu()
    {
        var items = new List<MenuItem>();
        foreach (var action in CurrentActions())
        {
            var item = new MenuItem { Header = action.Name, Tag = action.Id, IsEnabled = action.IsEnabled };
            AutomationProperties.SetHelpText(item, action.Description);
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
        if (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
        {
            e.Handled = true;
        }
        else if (e.Key == Key.A && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            ResultsGrid.SelectAll();
        }
        else if (e.Key == Key.Enter) { e.Handled = true; await SafeAsync(DefaultActionAsync); }
        else if (e.Key == Key.C && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            var action = CurrentActions().FirstOrDefault(action => action.Id == ConsoleActionId.Copy);
            if (action is not null) await SafeAsync(() => InvokeActionAsync(action));
        }
    }

    private void ResultKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Apps && !(e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)) return;
        e.Handled = true;
        PopulateContextMenu();
        // Avalonia dismisses an open context menu on the opening gesture's key-up.
        ResultContextMenu.Placement = PlacementMode.Bottom;
        ResultContextMenu.Open(ResultsGrid);
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { e.Handled = true; await SafeAsync(RefreshAsync); }
        else if (e.Key == Key.F && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        { e.Handled = true; DocumentTabs.SelectedIndex = 0; FilterBox.Focus(); }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Left or Key.Right)
        { e.Handled = true; await SafeAsync(() => MoveHistoryAsync(e.Key == Key.Left ? -1 : 1)); }
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _historyPosition > 0;
        ForwardButton.IsEnabled = _historyPosition >= 0 && _historyPosition < _navigationHistory.Count - 1;
        ToolTip.SetTip(BackButton, $"Back requeries; no related-result objects are cached. Latest {RetentionPolicy.NavigationRoutes} routes retained." +
            (_navigationEvictions == 0 ? string.Empty : $" {_navigationEvictions} older routes evicted; use Location to requery."));
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

    private void ReportError(Exception exception, bool updateView = true)
    {
        Trace.TraceError(exception.ToString());
        _diagnosticBuffer.Add(new(DateTimeOffset.Now, "Error", exception.ToString()));
        UpdateDiagnosticsText();
        DiagnosticsPane.IsVisible = true;
        if (!updateView) return;
        _navigationWarning = null;
        _model.Status = $"Error: {exception.Message}";
        ResultMessage.IsVisible = true;
        ResultMessageText.Text = exception.Message;
    }

    private void ReleaseCurrentResult()
    {
        if (_result is not null) _session?.ReleaseResult(_result.Id);
        _result = null;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_readyToClose) return;
        e.Cancel = true;
        if (_closing || _confirmingClose) return;
        try { SaveWorkspace(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportError(exception);
            _confirmingClose = true;
            bool closeWithoutSaving;
            try
            {
                closeWithoutSaving = await Dialogs.ConfirmAsync(this, "Workspace was not saved",
                    $"{exception.Message}\n\nClose without saving? Cancel keeps the window open so you can recover or retry.",
                    acceptLabel: "Close without saving");
            }
            finally { _confirmingClose = false; }
            if (!closeWithoutSaving)
            {
                _model.Status = "Workspace was not saved. Window remains open; repair storage and retry.";
                return;
            }
        }
        _closing = true;
        _lifetime.Cancel();
        _active?.Cancel();
        await SafeAsync(async () =>
        {
            ReleaseCurrentResult();
            if (_session is not null) await _session.DisposeAsync();
        });
        if (_session is IInvocationHostSession interactive)
        {
            interactive.StateChanged -= InvocationStateChanged;
            interactive.PromptHandler = null;
        }
        _lifetime.Dispose();
        _readyToClose = true;
        Close();
    }

    private async void RefreshClick(object? sender, RoutedEventArgs e) => await SafeAsync(RefreshAsync);
    private void StopClick(object? sender, RoutedEventArgs e)
    {
        var active = _active;
        _model.Status = "Stopping...";
        active?.Cancel();
        if (_promptInvocationId is { } id && _session is IInvocationHostSession interactive)
            interactive.StopInvocation(id);
        if (active is not null) _ = ReportStoppedOperationAsync(active, _generation);
    }
    private async Task ReportStoppedOperationAsync(CancellationTokenSource active, long generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (!_closing && generation == _generation && _active == active && _model.IsBusy && active.IsCancellationRequested)
            _model.Status = UnresponsiveStatus;
    }
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
    private void HighContrastClick(object? sender, RoutedEventArgs e) =>
        RequestedThemeVariant = HighContrastMenu.IsChecked ? App.HighContrastTheme : ThemeVariant.Light;
    private void ExitClick(object? sender, RoutedEventArgs e) => Close();
    private async void ResetLayoutClick(object? sender, RoutedEventArgs e) => await WorkspaceOperationAsync(() =>
    {
        _workspace = _workspace with { Layout = new() };
        WindowState = WindowState.Normal;
        ApplyWorkspaceLayout();
        SaveWorkspace();
        UpdateWorkspaceNotice();
        return Task.CompletedTask;
    });
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
        "An original, cross platform console inspired by PowerGUI"));
    private async void ColumnsClick(object? sender, RoutedEventArgs e) =>
        await SafeAsync(() => Dialogs.ColumnsAsync(this, ResultsGrid, _columns));
}
