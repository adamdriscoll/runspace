using System.ComponentModel;
using System.Text.Json;
using Avalonia.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Runspace.Core;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;

namespace Runspace.Desktop.Tests;

public sealed partial class ConsoleTests
{
    private sealed class WorkspaceFixtureStorage : IWorkspaceStorage
    {
        public Dictionary<string, string> Files { get; } = [];
        public bool FailSave { get; set; }
        public string? Read(string name) => Files.GetValueOrDefault(name);
        public void WriteAtomic(string name, string content)
        {
            if (FailSave) throw new IOException("Fixture storage is read-only.");
            Files[name] = content;
        }
        public string Archive(string name)
        {
            var backup = name + ".fixture.bak";
            Files[backup] = Files[name];
            Files.Remove(name);
            return backup;
        }
        public WorkspaceDocument Saved => JsonSerializer.Deserialize<WorkspaceDocument>(Files[WorkspaceStore.FileName])!;
    }

    private static void SelectResource(MainWindow window, ResourceKind kind)
    {
        var model = (ConsoleViewModel)window.DataContext!;
        window.FindControl<TreeView>("NavigationTree")!.SelectedItem =
            model.Roots[0].Children.Single(item => item.Node.Kind == kind);
    }

    private static void WorkspaceMenu(MainWindow window, string name) =>
        window.FindControl<MenuItem>(name)!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void DialogButton(Window dialog, string name) =>
        Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == name));

    [AvaloniaFact]
    public async Task WorkspaceRoundTripRestoresPerViewPreferencesButDoesNotExecuteUntilUserOpensResource()
    {
        var storage = new WorkspaceFixtureStorage();
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        Assert.True(window.IsSessionReady);
        Assert.Equal(0, session.Queries);
        Assert.Equal(0, session.DriveReads);
        SelectResource(window, ResourceKind.Processes);
        window.UpdateLayout();
        var grid = window.FindControl<DataGrid>("ResultsGrid")!;
        grid.Columns[1].DisplayIndex = 0;
        grid.Columns[1].Width = new DataGridLength(88);
        grid.Columns[1].Sort(ListSortDirection.Ascending);
        await UntilAsync(() => ((DataGridCollectionView)grid.ItemsSource!).SortDescriptions.Count == 1);
        grid.Columns[0].IsVisible = false;
        window.FindControl<TextBox>("FilterBox")!.Text = "beta";
        SelectResource(window, ResourceKind.Services);
        Assert.Equal(string.Empty, window.FindControl<TextBox>("FilterBox")!.Text);
        Assert.All(grid.Columns, column => Assert.True(column.IsVisible));
        window.FindControl<TextBox>("FilterBox")!.Text = "alpha";
        grid.Columns[0].Width = new DataGridLength(245);
        window.Close();
        await UntilAsync(() => session.Disposed);
        Assert.Equal("services", storage.Saved.ActiveView!.ResourceId);
        Assert.Equal(2, storage.Saved.Views.Count);
        Assert.Equal(new WorkspaceSort("Id", false), Assert.Single(storage.Saved.Views.Single(
            view => view.Reference.ResourceId == "processes").Sorting));
        var serialized = storage.Files[WorkspaceStore.FileName];
        Assert.DoesNotContain("Get-Process", serialized);
        Assert.DoesNotContain("Handle", serialized);
        Assert.DoesNotContain("Script", serialized);
        Assert.DoesNotContain("Diagnostics\"", serialized);
        Assert.DoesNotContain("History", serialized);

        var restarted = new FixtureSession();
        window = new MainWindow(restarted, workspaceStore: new(storage));
        window.Show();
        try
        {
            Assert.Equal(0, restarted.Queries);
            Assert.Equal(0, restarted.DriveReads);
            Assert.Null(window.FindControl<DataGrid>("ResultsGrid")!.ItemsSource);
            Assert.Null(window.FindControl<TreeView>("NavigationTree")!.SelectedItem);
            Assert.Empty(((ConsoleViewModel)window.DataContext!).History);
            Assert.True(window.FindControl<Border>("WorkspaceNotice")!.IsVisible);
            Assert.True(window.FindControl<Button>("OpenSavedViewButton")!.IsVisible);
            Click(window.FindControl<Button>("OpenSavedViewButton")!);
            window.UpdateLayout();
            Assert.Equal(1, restarted.Queries);
            Assert.Equal(ResourceKind.Services, Assert.Single(restarted.QueryNodes).Kind);
            Assert.Equal("alpha", window.FindControl<TextBox>("FilterBox")!.Text);
            grid = window.FindControl<DataGrid>("ResultsGrid")!;
            Assert.Equal(245, grid.Columns[0].Width.Value);
            SelectResource(window, ResourceKind.Processes);
            window.UpdateLayout();
            Assert.Equal("beta", window.FindControl<TextBox>("FilterBox")!.Text);
            Assert.Equal(0, grid.Columns[1].DisplayIndex);
            Assert.Equal(88, grid.Columns[1].Width.Value);
            Assert.False(grid.Columns[0].IsVisible);
            window.FindControl<TextBox>("FilterBox")!.Text = string.Empty;
            Assert.Equal(2, grid.ItemsSource!.Cast<ConsoleRow>().First().Cells["Id"].Value);
            Assert.Equal(0, restarted.DriveReads);
            Assert.Null(restarted.LastOutcome);
            Assert.False(window.FindControl<Border>("WorkspaceNotice")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MissingSessionKitAndResourceReferencesStayVisibleAcrossRestartUntilConfirmedCleanup()
    {
        ResourceReference[] missing = [new("offline-session", "builtin.local-system", "processes"),
            new("local", "missing-kit", "processes"), ResourceReference.BuiltIn("removed-resource")];
        var document = new WorkspaceDocument
        {
            ActiveView = missing[0], Views = missing.Select(reference => new WorkspaceView(reference, [], [], "")).ToArray()
        };
        var storage = new WorkspaceFixtureStorage();
        storage.Files[WorkspaceStore.FileName] = JsonSerializer.Serialize(document);
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        try
        {
            Assert.Equal(0, session.Queries);
            Assert.Equal(0, session.DriveReads);
            Assert.True(window.FindControl<Border>("WorkspaceNotice")!.IsVisible);
            Assert.Contains("3 unresolved", window.FindControl<TextBlock>("WorkspaceNoticeText")!.Text);
            Assert.False(window.FindControl<Button>("OpenSavedViewButton")!.IsVisible);
            WorkspaceMenu(window, "WorkspaceSettingsMenu");
            var settings = window.OwnedWindows.Single();
            Assert.Contains("offline-session", settings.GetLogicalDescendants().OfType<TextBlock>().First().Text);
            DialogButton(settings, "WorkspaceSettingsClose");
        }
        finally { window.Close(); }
        await UntilAsync(() => session.Disposed);
        Assert.Equal(missing, storage.Saved.Views.Select(view => view.Reference));

        session = new FixtureSession();
        window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        try
        {
            WorkspaceMenu(window, "WorkspaceSettingsMenu");
            DialogButton(window.OwnedWindows.Single(), "RemoveMissingReferences");
            var confirmation = window.OwnedWindows.Single();
            DialogButton(confirmation, "ConfirmationAccept");
            await UntilAsync(() => storage.Saved.Views.Count == 0);
            Assert.Empty(storage.Saved.Views);
            Assert.Null(storage.Saved.ActiveView);
            Assert.False(window.FindControl<Border>("WorkspaceNotice")!.IsVisible);
            Assert.Equal(0, session.Queries);
            Assert.Equal(0, session.DriveReads);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(WorkspaceStore.FileName)]
    [InlineData(WorkspaceStore.LegacyFileName)]
    public async Task ResetLayoutAndCloseNeverOverwriteDamagedData(string fileName)
    {
        var storage = new WorkspaceFixtureStorage();
        storage.Files[fileName] = "{damaged";
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        Assert.True(window.FindControl<Border>("WorkspaceNotice")!.IsVisible);
        WorkspaceMenu(window, "ResetLayoutMenu");
        Assert.Equal("{damaged", storage.Files[fileName]);
        Assert.True(window.FindControl<Border>("WorkspaceNotice")!.IsVisible);
        window.Close();
        await UntilAsync(() => session.Disposed);
        Assert.Equal("{damaged", storage.Files[fileName]);
        Assert.Equal(0, session.Queries);
        Assert.Single(storage.Files);
    }

    [AvaloniaFact]
    public async Task RecoveryArchivesOriginalBeforeSavingDefaultsAndNeverRunsCode()
    {
        var storage = new WorkspaceFixtureStorage();
        storage.Files[WorkspaceStore.FileName] = "{\"Version\":999}";
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        try
        {
            WorkspaceMenu(window, "WorkspaceSettingsMenu");
            DialogButton(window.OwnedWindows.Single(), "RecoverWorkspace");
            var confirmation = window.OwnedWindows.Single();
            DialogButton(confirmation, "ConfirmationAccept");
            await UntilAsync(() => storage.Files.ContainsKey(WorkspaceStore.FileName + ".fixture.bak"));
            Assert.Equal("{\"Version\":999}", storage.Files[WorkspaceStore.FileName + ".fixture.bak"]);
            Assert.Equal(new WorkspaceLayout(), storage.Saved.Layout with { X = null, Y = null });
            Assert.False(window.FindControl<Border>("WorkspaceNotice")!.IsVisible);
            var message = window.OwnedWindows.Single();
            Click(message.GetLogicalDescendants().OfType<Button>().Single());
            Assert.Equal(0, session.Queries);
            Assert.Equal(0, session.DriveReads);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ResetLayoutPersistsDefaultProportionsAndVisibilityWithoutRemovingViewOverrides()
    {
        var storage = new WorkspaceFixtureStorage();
        var document = new WorkspaceDocument
        {
            Layout = new() { NavigationWidth = 300, ActionsWidth = 310, DiagnosticsVisible = true, ActionsVisible = false, HighContrast = true },
            ActiveView = ResourceReference.BuiltIn("processes"),
            Views = [new(ResourceReference.BuiltIn("processes"), [new("Name", true, 245)], [], "beta")]
        };
        storage.Files[WorkspaceStore.FileName] = JsonSerializer.Serialize(document);
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        Assert.False(window.FindControl<Border>("ActionsPane")!.IsVisible);
        Assert.True(window.FindControl<Border>("DiagnosticsPane")!.IsVisible);
        Assert.Equal(App.HighContrastTheme, window.ActualThemeVariant);
        WorkspaceMenu(window, "ResetLayoutMenu");
        Assert.Equal(new WorkspaceLayout(), storage.Saved.Layout with { X = null, Y = null });
        Assert.Equal("beta", Assert.Single(storage.Saved.Views).Filter);
        Assert.Equal(document.ActiveView, storage.Saved.ActiveView);
        Assert.True(window.FindControl<Border>("ActionsPane")!.IsVisible);
        Assert.Equal(0, session.Queries);
        window.Close();
        await UntilAsync(() => session.Disposed);
        session = new FixtureSession();
        window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        try
        {
            Assert.Equal(220, window.FindControl<Grid>("WorkspaceGrid")!.ColumnDefinitions[4].Width.Value);
            Assert.True(window.FindControl<Border>("ActionsPane")!.IsVisible);
            Assert.False(window.FindControl<Border>("DiagnosticsPane")!.IsVisible);
            Assert.Equal(0, session.Queries);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task UnavailableColumnPreferencesAreRetainedAndVisibleFallbackIsExplained()
    {
        var storage = new WorkspaceFixtureStorage();
        storage.Files[WorkspaceStore.FileName] = JsonSerializer.Serialize(new WorkspaceDocument
        {
            Views = [new(ResourceReference.BuiltIn("processes"),
                [new("removed-column", true, 250), new("Name", false, 100), new("Id", false, 80)],
                [new("removed-column", false)], "")]
        });
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        SelectResource(window, ResourceKind.Processes);
        Assert.True(window.FindControl<DataGrid>("ResultsGrid")!.Columns[0].IsVisible);
        Assert.Contains("Saved visible columns are unavailable", ((ConsoleViewModel)window.DataContext!).Diagnostics);
        window.Close();
        await UntilAsync(() => session.Disposed);
        var view = Assert.Single(storage.Saved.Views);
        Assert.Equal(["removed-column", "Name", "Id"], view.Columns.Select(column => column.Key));
        Assert.Contains(view.Columns, column => column.Key == "removed-column" && column.Visible);
        Assert.Contains(view.Sorting, sort => sort.Key == "removed-column");
    }

    [AvaloniaFact]
    public async Task DynamicSessionPathsAndRelatedLiveHandlesAreNotPersisted()
    {
        var storage = new WorkspaceFixtureStorage();
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        TestNavigation.ShowResource(window);
        var model = (ConsoleViewModel)window.DataContext!;
        var dynamic = new NavigationItem(new("path:private-session-path", "Private path", ResourceKind.ProviderPath,
            "Private session location", "private-session-path"));
        model.Roots[2].Children.Add(dynamic);
        window.FindControl<TreeView>("NavigationTree")!.SelectedItem = dynamic;
        window.Close();
        await UntilAsync(() => session.Disposed);
        Assert.Null(storage.Saved.ActiveView);
        Assert.DoesNotContain("private-session-path", storage.Files[WorkspaceStore.FileName]);

        session = new FixtureSession();
        window = new MainWindow(session, workspaceStore: new(storage));
        TestNavigation.ShowResource(window);
        var grid = window.FindControl<DataGrid>("ResultsGrid")!;
        var row = grid.ItemsSource!.Cast<ConsoleRow>().First();
        grid.SelectedItem = row;
        await window.InvokeActionAsync(new(ConsoleActionId.ProcessModules, "Related", "General", "Fixture", true,
            ResultPolicy: ActionResultPolicy.Related));
        window.Close();
        await UntilAsync(() => session.Disposed);
        Assert.Null(storage.Saved.ActiveView);
        Assert.DoesNotContain(row.Handle.ToString(), storage.Files[WorkspaceStore.FileName]);
    }

    [AvaloniaFact]
    public async Task SaveFailureLeavesWindowOpenWhenUserCancelsClose()
    {
        var storage = new WorkspaceFixtureStorage();
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        storage.FailSave = true;
        window.Close();
        Assert.True(window.IsVisible);
        Assert.False(session.Disposed);
        var prompt = window.OwnedWindows.Single();
        Click(prompt.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel")));
        await UntilAsync(() => ((ConsoleViewModel)window.DataContext!).Status.Contains("Window remains open"));
        Assert.True(window.IsVisible);
        Assert.Contains("Fixture storage is read-only", ((ConsoleViewModel)window.DataContext!).Diagnostics);
        storage.FailSave = false;
        window.Close();
        await UntilAsync(() => session.Disposed);
        Assert.True(storage.Files.ContainsKey(WorkspaceStore.FileName));
    }

    [AvaloniaFact]
    public async Task MultipleTypedSortKeysKeepTheirPriorityAcrossRestart()
    {
        var storage = new WorkspaceFixtureStorage();
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        SelectResource(window, ResourceKind.Processes);
        window.UpdateLayout();
        var grid = window.FindControl<DataGrid>("ResultsGrid")!;
        var view = (DataGridCollectionView)grid.ItemsSource!;
        view.SortDescriptions.Add(DataGridSortDescription.FromComparer(grid.Columns[1].CustomSortComparer, ListSortDirection.Ascending));
        view.SortDescriptions.Add(DataGridSortDescription.FromComparer(grid.Columns[0].CustomSortComparer, ListSortDirection.Descending));
        window.Close();
        await UntilAsync(() => session.Disposed);
        Assert.Equal([new WorkspaceSort("Id", false), new("Name", true)], Assert.Single(storage.Saved.Views).Sorting);
        session = new FixtureSession();
        window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        try
        {
            Click(window.FindControl<Button>("OpenSavedViewButton")!);
            window.UpdateLayout();
            grid = window.FindControl<DataGrid>("ResultsGrid")!;
            view = (DataGridCollectionView)grid.ItemsSource!;
            Assert.Equal(2, view.SortDescriptions.Count);
            Assert.Same(grid.Columns[1].CustomSortComparer, Assert.IsType<DataGridComparerSortDescription>(view.SortDescriptions[0]).SourceComparer);
            Assert.Equal(ListSortDirection.Ascending, view.SortDescriptions[0].Direction);
            Assert.Same(grid.Columns[0].CustomSortComparer, Assert.IsType<DataGridComparerSortDescription>(view.SortDescriptions[1]).SourceComparer);
            Assert.Equal(2, view.Cast<ConsoleRow>().First().Cells["Id"].Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReloadAfterExternalRepairRestoresPreferencesWithoutDiscoveringDrivesOrQuerying()
    {
        var storage = new WorkspaceFixtureStorage();
        storage.Files[WorkspaceStore.FileName] = "{";
        var session = new FixtureSession();
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        try
        {
            storage.Files[WorkspaceStore.FileName] = JsonSerializer.Serialize(new WorkspaceDocument
            {
                Layout = new() { ActionsWidth = 310, NavigationVisible = false },
                ActiveView = ResourceReference.BuiltIn("environment")
            });
            WorkspaceMenu(window, "WorkspaceSettingsMenu");
            DialogButton(window.OwnedWindows.Single(), "ReloadWorkspace");
            await UntilAsync(() => ((ConsoleViewModel)window.DataContext!).Status.StartsWith("Workspace reloaded"));
            Assert.Equal(310, window.FindControl<Grid>("WorkspaceGrid")!.ColumnDefinitions[4].Width.Value);
            Assert.False(window.FindControl<Border>("NavigationPane")!.IsVisible);
            Assert.True(window.FindControl<Button>("OpenSavedViewButton")!.IsVisible);
            Assert.DoesNotContain(storage.Files.Keys, name => name.EndsWith(".bak"));
            Assert.Equal(0, session.Queries);
            Assert.Equal(0, session.DriveReads);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingDuringPendingNavigationKeepsTheLastAcceptedViewPreferences()
    {
        var storage = new WorkspaceFixtureStorage();
        var pending = new TaskCompletionSource<ConsoleResult>();
        var session = new FixtureSession
        {
            Query = node => node.Kind == ResourceKind.Services ? pending.Task : Task.FromResult(DenseFixture())
        };
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        SelectResource(window, ResourceKind.Processes);
        window.FindControl<TextBox>("FilterBox")!.Text = "fixture-0001";
        SelectResource(window, ResourceKind.Services);
        Assert.True(((ConsoleViewModel)window.DataContext!).IsBusy);
        window.Close();
        await UntilAsync(() => session.Disposed);
        Assert.Equal("processes", storage.Saved.ActiveView!.ResourceId);
        Assert.Equal("fixture-0001", Assert.Single(storage.Saved.Views).Filter);
        pending.SetResult(DenseFixture());
        await UntilAsync(() => session.Released.Count == 2);
    }

    [AvaloniaFact]
    public async Task FailedResetSaveDoesNotEnableActionsDuringAnActiveQuery()
    {
        var pending = new TaskCompletionSource<ConsoleResult>();
        var storage = new WorkspaceFixtureStorage { FailSave = true };
        var session = new FixtureSession { Query = _ => pending.Task };
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        try
        {
            SelectResource(window, ResourceKind.Processes);
            WorkspaceMenu(window, "ResetLayoutMenu");
            Assert.True(((ConsoleViewModel)window.DataContext!).IsBusy);
            Assert.True(window.FindControl<Button>("StopButton")!.IsEnabled);
            Assert.Contains("read-only", ((ConsoleViewModel)window.DataContext!).Diagnostics);
            pending.SetResult(DenseFixture());
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
        }
        finally
        {
            storage.FailSave = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task QueryCanCompleteWhileFailedSaveCloseDecisionIsPending()
    {
        var pending = new TaskCompletionSource<ConsoleResult>();
        var storage = new WorkspaceFixtureStorage { FailSave = true };
        var session = new FixtureSession { Query = _ => pending.Task };
        var window = new MainWindow(session, workspaceStore: new(storage));
        window.Show();
        SelectResource(window, ResourceKind.Processes);
        window.Close();
        var confirmation = window.OwnedWindows.Single();
        pending.SetResult(DenseFixture());
        await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
        DialogButton(confirmation, "ConfirmationCancel");
        await UntilAsync(() => ((ConsoleViewModel)window.DataContext!).Status.Contains("Window remains open"));
        Assert.True(window.IsVisible);
        Assert.False(session.Disposed);
        Assert.NotEmpty(window.FindControl<DataGrid>("ResultsGrid")!.ItemsSource!.Cast<ConsoleRow>());
        storage.FailSave = false;
        window.Close();
        await UntilAsync(() => session.Disposed);
    }
}
