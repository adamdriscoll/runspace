using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Runspace.Core;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;

[assembly: AvaloniaTestApplication(typeof(Runspace.Desktop.Tests.TestApplication))]

namespace Runspace.Desktop.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class ConsoleTests
{
    [AvaloniaFact]
    public async Task StartupDisplaysProcessesAndAllBuiltInResources()
    {
        var window = new MainWindow(new FixtureSession());
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        var tree = window.FindControl<TreeView>("NavigationTree")!;
        var roots = tree.ItemsSource!.Cast<NavigationItem>().ToArray();
        Assert.Equal(["Local System", "Network", "PowerShell Drives"], roots.Select(item => item.Name));
        Assert.Contains(roots[0].Children, item => item.Name == "WMI Browser");
        Assert.Contains(roots[0].Children, item => item.Name == "Services");
        Assert.Equal(2, window.FindControl<DataGrid>("ResultsGrid")!.ItemsSource!.Cast<ConsoleRow>().Count());
        window.Close();
    }

    [AvaloniaFact]
    public async Task FilteringClearsHiddenSelectionAndUpdatesActions()
    {
        var window = new MainWindow(new FixtureSession());
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        var grid = window.FindControl<DataGrid>("ResultsGrid")!;
        grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
        var actions = window.FindControl<StackPanel>("ActionsPanel")!;
        Assert.True(actions.Children.OfType<Button>().Single(button => Equals(button.Tag, ConsoleActionId.Properties)).IsEnabled);
        window.FindControl<TextBox>("FilterBox")!.Text = "beta";
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Empty(grid.SelectedItems);
        Assert.Single(grid.ItemsSource!.Cast<ConsoleRow>());
        Assert.False(actions.Children.OfType<Button>().Single(button => Equals(button.Tag, ConsoleActionId.Properties)).IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TableSortsNumericIdsRatherThanDisplayedStrings()
    {
        var window = new MainWindow(new FixtureSession());
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        var grid = window.FindControl<DataGrid>("ResultsGrid")!;
        grid.Columns.Single(column => Equals(column.Header, "Id")).Sort(ListSortDirection.Ascending);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(2, grid.ItemsSource!.Cast<ConsoleRow>().First().Cells["Id"].Value);
        grid.Columns.Single(column => Equals(column.Header, "Id")).Sort(ListSortDirection.Descending);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(100, grid.ItemsSource!.Cast<ConsoleRow>().First().Cells["Id"].Value);
        window.Close();
    }

    private sealed class FixtureSession : IConsoleSession
    {
        public string RuntimeVersion => "fixture";
        public Task<ConsoleResult> QueryAsync(ConsoleNode node, CancellationToken cancellationToken = default)
        {
            ConsoleColumn[] columns = [new("Name", "Name"), new("Id", "Id", ColumnKind.Number)];
            ConsoleRow[] rows = [Row("alpha", 100), Row("beta", 2)];
            return Task.FromResult(new ConsoleResult(Guid.NewGuid(), columns, rows, "Get-Process", [], TimeSpan.Zero, InvocationOutcome.Completed));
        }
        private static ConsoleRow Row(string name, int id) => new(Guid.NewGuid(), new Dictionary<string, ConsoleCell>
        {
            ["Name"] = ConsoleCell.From(name), ["Id"] = ConsoleCell.From(id)
        });
        public Task<ConsoleResult> ExecuteAsync(ConsoleActionId action, Guid resultId, IReadOnlyList<Guid> selection,
            IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Mutation is not part of this UI fixture.");
        public Task<IReadOnlyList<ObjectProperty>> InspectAsync(Guid resultId, Guid handle, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ObjectProperty>>([new("Name", "string", "fixture")]);
        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderInfo>>([new("Environment", "ShouldProcess")]);
        public Task<IReadOnlyList<ConsoleNode>> GetDriveNodesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConsoleNode>>([new("env", "Env:", ResourceKind.ProviderPath, "Environment", "Env:")]);
        public void ReleaseResult(Guid resultId) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
