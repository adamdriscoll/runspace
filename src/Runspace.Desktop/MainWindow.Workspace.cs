using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Runspace.Core;
using Runspace.Desktop.ViewModels;

namespace Runspace.Desktop;

public partial class MainWindow
{
    private readonly WorkspaceStore? _workspaceStore;
    private WorkspaceDocument _workspace = new();
    private ResourceReference? _displayedReference;
    private string? _workspaceRecovery;
    private Size? _normalSize;
    private PixelPoint? _normalPosition;

    private static ResourceReference? StableReference(ConsoleNode? node) =>
        node is not null && (BuiltInCatalog.LocalSystem.Any(resource => resource.Id == node.Id) ||
            node.Id is "local" or "network" or "accounts" or "network-root" or "managed-computers" or "provider-drives")
            ? ResourceReference.BuiltIn(node.Id) : null;

    private NavigationItem? ResolveReference(ResourceReference reference) =>
        reference.SessionId == "local" && reference.KitId == "builtin.local-system" &&
        FindNode(_model.Roots, reference.ResourceId) is { } item && StableReference(item.Node) is not null ? item : null;

    private IReadOnlyList<ResourceReference> MissingReferences() =>
        WorkspaceStore.Unresolved(_workspace, reference => ResolveReference(reference) is not null);

    private void LoadWorkspace()
    {
        if (_workspaceStore is null) return;
        var load = _workspaceStore.Load();
        _workspace = load.Document;
        _workspaceRecovery = load.RecoveryMessage;
        ApplyWorkspaceLayout();
        if (_workspaceRecovery is not null) ReportError(new InvalidDataException(_workspaceRecovery));
        UpdateWorkspaceNotice();
    }

    private void ApplyWorkspaceLayout()
    {
        var layout = _workspace.Layout;
        var displays = Screens.All.OrderByDescending(screen => screen.IsPrimary).Select(screen =>
            new WorkspaceDisplay(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width,
                screen.WorkingArea.Height, screen.Scaling)).ToArray();
        if (displays.Length > 0)
        {
            var placement = WorkspacePlacement.Fit(layout, displays);
            MinWidth = Math.Min(900, placement.Width);
            MinHeight = Math.Min(600, placement.Height);
            Width = placement.Width;
            Height = placement.Height;
            Position = new PixelPoint(placement.X, placement.Y);
            _normalPosition = Position;
            _normalSize = new Size(Width, Height);
        }
        else
        {
            Width = Math.Clamp(layout.Width, 900, 2000);
            Height = Math.Clamp(layout.Height, 600, 1400);
        }
        WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        NavigationPaneMenu.IsChecked = layout.NavigationVisible;
        ActionsPaneMenu.IsChecked = layout.ActionsVisible;
        ApplyPaneVisibility(layout);
        DiagnosticsPane.Height = layout.DiagnosticsHeight;
        DiagnosticsPane.IsVisible = layout.DiagnosticsVisible;
        HighContrastMenu.IsChecked = layout.HighContrast;
        RequestedThemeVariant = layout.HighContrast ? App.HighContrastTheme : ThemeVariant.Light;
    }

    private void ApplyPaneVisibility(WorkspaceLayout layout)
    {
        void SetPane(int column, double width, double minimum, bool visible, Control pane, Control splitter)
        {
            var definition = WorkspaceGrid.ColumnDefinitions[column];
            definition.MinWidth = visible ? minimum : 0;
            definition.Width = new GridLength(visible ? width : 0);
            WorkspaceGrid.ColumnDefinitions[column == 0 ? 1 : 3].Width = new GridLength(visible ? 4 : 0);
            pane.IsVisible = splitter.IsVisible = visible;
        }
        SetPane(0, layout.NavigationWidth, 160, layout.NavigationVisible, NavigationPane, NavigationSplitter);
        SetPane(4, layout.ActionsWidth, 180, layout.ActionsVisible, ActionsPane, ActionsSplitter);
    }

    private WorkspaceLayout CaptureLayout()
    {
        double PaneWidth(int column, double minimum) => Math.Clamp(
            WorkspaceGrid.ColumnDefinitions[column].Width.IsAbsolute
                ? WorkspaceGrid.ColumnDefinitions[column].Width.Value : WorkspaceGrid.ColumnDefinitions[column].ActualWidth,
            minimum, 350);
        if (WindowState == WindowState.Normal)
        {
            _normalSize = new Size(Width, Height);
            _normalPosition = Position;
        }
        return _workspace.Layout with
        {
            Width = Math.Clamp(_normalSize?.Width ?? _workspace.Layout.Width, 1, 2000),
            Height = Math.Clamp(_normalSize?.Height ?? _workspace.Layout.Height, 1, 1400),
            X = _normalPosition?.X ?? _workspace.Layout.X, Y = _normalPosition?.Y ?? _workspace.Layout.Y,
            NavigationWidth = NavigationPane.IsVisible
                ? PaneWidth(0, 160) : _workspace.Layout.NavigationWidth,
            ActionsWidth = ActionsPane.IsVisible
                ? PaneWidth(4, 180) : _workspace.Layout.ActionsWidth,
            NavigationVisible = NavigationPane.IsVisible, ActionsVisible = ActionsPane.IsVisible,
            DiagnosticsVisible = DiagnosticsPane.IsVisible,
            DiagnosticsHeight = Math.Clamp(DiagnosticsPane.Height, 80, 500),
            HighContrast = RequestedThemeVariant == App.HighContrastTheme
        };
    }

    private void CaptureViewPreferences()
    {
        if (_displayedReference is not { } reference || _view is null) return;
        var previous = _workspace.Views.FirstOrDefault(view => view.Reference == reference);
        var current = ResultsGrid.Columns.OrderBy(column => column.DisplayIndex)
            .Select(column => new WorkspaceColumn((string)column.Tag!, column.IsVisible,
                Math.Clamp(column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth, 40, 1200))).ToArray();
        var preservedColumns = MergeUnavailablePreferences(previous?.Columns ?? [], current,
            saved => current.Any(item => item.Key == saved.Key));
        var sorting = _view.SortDescriptions.OfType<DataGridComparerSortDescription>().Select(sort =>
        {
            var column = ResultsGrid.Columns.FirstOrDefault(column => ReferenceEquals(column.CustomSortComparer, sort.SourceComparer));
            return column?.Tag is string key ? new WorkspaceSort(key, sort.Direction == ListSortDirection.Descending) : null;
        }).OfType<WorkspaceSort>().ToArray();
        var preservedSorts = MergeUnavailablePreferences(previous?.Sorting ?? [], sorting,
            saved => ResultsGrid.Columns.Any(column => Equals(column.Tag, saved.Key)));
        var preference = new WorkspaceView(reference, preservedColumns, preservedSorts, FilterBox.Text ?? string.Empty);
        _workspace = _workspace with
        {
            Views = [.. _workspace.Views.Where(view => view.Reference != reference), preference]
        };
    }

    private static IReadOnlyList<T> MergeUnavailablePreferences<T>(IReadOnlyList<T> previous,
        IReadOnlyList<T> current, Func<T, bool> isAvailable)
    {
        // Keep unavailable entries in their saved slots; available entries follow the user's current order.
        var ordered = new Queue<T>(current);
        var merged = new List<T>();
        foreach (var saved in previous)
        {
            if (!isAvailable(saved)) merged.Add(saved);
            else if (ordered.TryDequeue(out var value)) merged.Add(value);
        }
        merged.AddRange(ordered);
        return merged;
    }

    private void ApplyViewPreferences()
    {
        if (_displayedReference is { } reference && _workspace.Views.FirstOrDefault(view => view.Reference == reference) is { } preference)
        {
            var index = 0;
            foreach (var saved in preference.Columns)
            {
                if (ResultsGrid.Columns.FirstOrDefault(column => Equals(column.Tag, saved.Key)) is not { } column) continue;
                column.DisplayIndex = index++;
                column.IsVisible = saved.Visible;
                column.Width = new DataGridLength(saved.Width);
            }
            if (ResultsGrid.Columns.Count > 0 && !ResultsGrid.Columns.Any(column => column.IsVisible))
            {
                ResultsGrid.Columns[0].IsVisible = true;
                _diagnosticBuffer.Add(new(DateTimeOffset.Now, "Workspace",
                    "Saved visible columns are unavailable; the first available column is shown. Use Columns to review preferences."));
                UpdateDiagnosticsText();
                DiagnosticsPane.IsVisible = true;
            }
            FilterBox.Text = preference.Filter;
            foreach (var sort in preference.Sorting)
            {
                if (ResultsGrid.Columns.FirstOrDefault(column => Equals(column.Tag, sort.Key)) is { } column)
                    _view!.SortDescriptions.Add(DataGridSortDescription.FromComparer(column.CustomSortComparer,
                        sort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            }
        }
        _workspace = _workspace with { ActiveView = _displayedReference };
        UpdateWorkspaceNotice();
    }

    private void SaveWorkspace()
    {
        if (_workspaceStore is null || _workspaceStore.IsWriteBlocked) return;
        CaptureViewPreferences();
        _workspace = _workspace with { Layout = CaptureLayout() };
        _workspaceStore.Save(_workspace);
    }

    private async Task WorkspaceOperationAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportError(exception);
        }
    }

    private void UpdateWorkspaceNotice()
    {
        var missing = MissingReferences();
        var saved = _currentNode is null && _workspace.ActiveView is not null;
        WorkspaceNotice.IsVisible = _workspaceRecovery is not null || missing.Count > 0 || saved;
        WorkspaceNoticeText.Text = _workspaceRecovery ??
            (missing.Count > 0 ? $"{missing.Count} unresolved saved resource/session/kit reference(s). Preferences are retained; review Workspace settings." :
                "Saved resource is paused. Open it deliberately to run its query.");
        OpenSavedViewButton.IsVisible = saved && _workspace.ActiveView is { } reference && ResolveReference(reference) is not null;
        OpenSavedViewButton.IsEnabled = IsSessionReady && !_model.IsBusy;
    }

    private async void OpenSavedViewClick(object? sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        if (_workspace.ActiveView is { } reference && ResolveReference(reference) is { } item && !_model.IsBusy)
            await NavigateAsync(item.Node);
    });

    private void PaneVisibilityClick(object? sender, RoutedEventArgs e)
    {
        _workspace = _workspace with
        {
            Layout = CaptureLayout() with
            {
                NavigationVisible = NavigationPaneMenu.IsChecked, ActionsVisible = ActionsPaneMenu.IsChecked
            }
        };
        ApplyPaneVisibility(_workspace.Layout);
    }

    private async void WorkspaceSettingsClick(object? sender, RoutedEventArgs e) => await WorkspaceOperationAsync(async () =>
    {
        var dialog = new Window
        {
            Title = "Workspace settings", Name = "WorkspaceSettings", Width = 650, Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant
        };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        var missing = MissingReferences();
        var text = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = (_workspaceRecovery ?? "Only preferences and stable references are saved. Saved references never start execution.") +
                "\nReload discards unsaved preferences and pauses the current view." +
                (missing.Count == 0 ? "\nNo unresolved references." : "\nUnresolved (session / kit / resource):\n" +
                    string.Join("\n", missing.Select(reference => $"{reference.SessionId} / {reference.KitId} / {reference.ResourceId}")))
        };
        panel.Children.Add(text);
        void Command(string name, string caption, bool enabled, Func<Task> action)
        {
            var button = new Button { Name = name, Content = caption, IsEnabled = enabled };
            button.Click += async (_, _) => await WorkspaceOperationAsync(async () => { dialog.Close(); await action(); });
            panel.Children.Add(button);
        }
        Command("ReloadWorkspace", "Reload repaired workspace", _workspaceStore is not null && !_model.IsBusy, () =>
        {
            ReleaseCurrentResult();
            _currentNode = null;
            _displayedReference = null;
            _view = null;
            _columns = [];
            _rows = [];
            ResultsGrid.ItemsSource = null;
            ResultsGrid.Columns.Clear();
            FilterBox.Text = string.Empty;
            NavigationTree.SelectedItem = null;
            _navigationHistory.Clear();
            _historyPosition = -1;
            UpdateNavigationButtons();
            LoadWorkspace();
            UpdateCounts();
            UpdateActions();
            _model.Status = "Workspace reloaded without running a query.";
            return Task.CompletedTask;
        });
        Command("RecoverWorkspace", "Back up damaged file and reset preferences...", _workspaceStore?.IsWriteBlocked == true, async () =>
        {
            if (!await Dialogs.ConfirmAsync(this, "Recover workspace",
                "Preserve the original file as a uniquely named .bak, then save default preferences? No code will run.",
                acceptLabel: "Back up and reset")) return;
            var backup = _workspaceStore!.BackUpDamagedFile();
            _diagnosticBuffer.Add(new(DateTimeOffset.Now, "Workspace", $"Original workspace preserved at {backup}"));
            UpdateDiagnosticsText();
            _workspaceRecovery = null;
            _workspace = new();
            _displayedReference = null;
            WindowState = WindowState.Normal;
            ApplyWorkspaceLayout();
            UpdateWorkspaceNotice();
            SaveWorkspace();
            UpdateWorkspaceNotice();
            _model.Status = $"Workspace reset. Original preserved at {backup}";
            await Dialogs.MessageAsync(this, "Workspace recovered", _model.Status);
        });
        Command("RemoveMissingReferences", "Remove unresolved references...", missing.Count > 0 && !_model.IsBusy, async () =>
        {
            if (!await Dialogs.ConfirmAsync(this, "Remove unresolved references",
                $"Remove these {missing.Count} unavailable references and their view overrides? This does not remove installed content or connect sessions.",
                acceptLabel: "Remove references")) return;
            _workspace = WorkspaceStore.RemoveReferences(_workspace, missing);
            SaveWorkspace();
            UpdateWorkspaceNotice();
        });
        Command("WorkspaceSettingsClose", "Close", true, () => Task.CompletedTask);
        dialog.Content = new ScrollViewer { Content = panel };
        await dialog.ShowDialog(this);
    });
}
