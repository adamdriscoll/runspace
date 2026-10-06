using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Runspace.Core;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;

namespace Runspace.Desktop.Tests;

public sealed partial class ConsoleTests
{
    [AvaloniaFact]
    public async Task RetentionNoticeAndStreamLossAreVisibleWithoutChangingTheOutcome()
    {
        var buffer = new DiagnosticBuffer();
        buffer.Add(new(DateTimeOffset.Now, "Error", "preserved-fixture-error"));
        for (var index = 0; index < 5000; index++)
            buffer.Add(new(DateTimeOffset.Now, "Warning", $"warning-{index}"));
        var result = ProviderResult() with
        {
            Diagnostics = buffer.Snapshot(),
            RetentionNotice = "Retained rows are incomplete. Export retained rows and requery a narrower scope.",
            Outcome = InvocationOutcome.CompletedWithErrors
        };
        var window = new MainWindow(new FixtureSession { Query = _ => Task.FromResult(result) });
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var model = (ConsoleViewModel)window.DataContext!;
            Assert.Contains("CompletedWithErrors", model.Status);
            Assert.True(window.FindControl<Border>("ResultMessage")!.IsVisible);
            Assert.Contains("Export retained", window.FindControl<TextBlock>("ResultMessageText")!.Text);
            Assert.True(window.FindControl<Border>("DiagnosticsPane")!.IsVisible);
            Assert.Contains("preserved-fixture-error", model.Diagnostics);
            Assert.Contains("evicted", model.Diagnostics);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NavigationCapsLoadedChildrenAndUnloadsCollapsedBranches()
    {
        var root = new ConsoleNode("root", "Fixture:", ResourceKind.ProviderPath, "Fixture", "Fixture:\\");
        var childRows = Enumerable.Range(0, 250).Select(index => new ConsoleRow(Guid.NewGuid(),
            new Dictionary<string, ConsoleCell> { ["Name"] = ConsoleCell.From($"child-{index}") },
            root with { Id = $"child-{index}", Name = $"child-{index}", Path = $"Fixture:\\child-{index}" })).ToArray();
        var result = ProviderResult(childRows);
        var session = new FixtureSession
        {
            DriveNodes = [root],
            Query = node => Task.FromResult(node == root ? result : ProviderResult())
        };
        var window = new MainWindow(session);
        TestNavigation.ShowResource(window);
        try
        {
            var model = (ConsoleViewModel)window.DataContext!;
            await UntilAsync(() => !model.IsBusy);
            window.UpdateLayout();
            var item = Assert.Single(model.Roots[2].Children);
            var container = window.FindControl<TreeView>("NavigationTree")!.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(control => ReferenceEquals(control.DataContext, item));
            container.IsExpanded = true;
            await UntilAsync(() => !item.IsLoading);
            Assert.Equal(RetentionPolicy.NavigationChildren, item.Children.Count(child => child.Node.Id != "loading"));
            Assert.Contains(item.Children, child => child.Name.Contains("Navigation limited"));
            Assert.Contains("limited to 200 children", model.Diagnostics);
            Assert.Contains(result.Id, session.Released);
            container.IsExpanded = false;
            Assert.Equal("loading", Assert.Single(item.Children).Node.Id);
            Assert.True(item.IsLazy);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NavigationRoutesAreBoundedAndOlderRouteLossRemainsVisible()
    {
        var session = new FixtureSession
        {
            Query = _ => Task.FromResult(ProviderResult(new ConsoleRow(Guid.NewGuid(),
                new Dictionary<string, ConsoleCell> { ["Name"] = ConsoleCell.From("source") })))
        };
        var window = new MainWindow(session);
        TestNavigation.ShowResource(window);
        try
        {
            var model = (ConsoleViewModel)window.DataContext!;
            await UntilAsync(() => !model.IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            for (var index = 0; index < 110; index++)
            {
                grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
                await window.InvokeActionAsync(new(ConsoleActionId.ProcessModules, "Related fixture", "Related information",
                    "Fixture", true, ResultPolicy: ActionResultPolicy.Related));
            }
            var back = window.FindControl<Button>("BackButton")!;
            Assert.Contains("older routes evicted", ToolTip.GetTip(back)?.ToString());
            var backQueries = session.Queries;
            for (var index = 0; index < RetentionPolicy.NavigationRoutes - 1; index++)
            {
                Assert.True(back.IsEnabled);
                Click(back);
                await UntilAsync(() => !model.IsBusy);
            }
            Assert.False(back.IsEnabled);
            Assert.Equal(backQueries + RetentionPolicy.NavigationRoutes - 1, session.Queries);
            Assert.Contains("older Back routes evicted", model.Status);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ExpandedNavigationHasAGlobalBudgetNotJustAPerBranchLimit()
    {
        var roots = Enumerable.Range(0, 6).Select(index =>
            new ConsoleNode($"root-{index}", $"Fixture{index}:", ResourceKind.ProviderPath,
                "Fixture", $"Fixture{index}:\\")).ToArray();
        var session = new FixtureSession
        {
            DriveNodes = roots,
            Query = node => Task.FromResult(ProviderResult(Enumerable.Range(0, 200).Select(index =>
                new ConsoleRow(Guid.NewGuid(), new Dictionary<string, ConsoleCell>(),
                    node with { Id = $"{node.Id}-{index}", Name = $"child-{index}" })).ToArray()))
        };
        var window = new MainWindow(session);
        TestNavigation.ShowResource(window);
        try
        {
            var model = (ConsoleViewModel)window.DataContext!;
            await UntilAsync(() => !model.IsBusy);
            foreach (var item in model.Roots[2].Children)
            {
                window.UpdateLayout();
                var container = window.FindControl<TreeView>("NavigationTree")!.GetVisualDescendants().OfType<TreeViewItem>()
                    .Single(control => ReferenceEquals(control.DataContext, item));
                container.IsExpanded = true;
                await UntilAsync(() => !item.IsLoading);
            }
            int Count(IEnumerable<NavigationItem> items) => items.Where(item => item.Node.Id != "loading")
                .Sum(item => 1 + Count(item.Children));
            Assert.Equal(RetentionPolicy.NavigationNodes, Count(model.Roots));
            Assert.Contains("Navigation limited", Assert.Single(model.Roots[2].Children[^1].Children).Name);
            Assert.Contains("1,000 loaded nodes", model.Diagnostics);
        }
        finally { window.Close(); }
    }
}
