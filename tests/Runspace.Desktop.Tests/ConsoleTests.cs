using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Runspace.Core;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;

[assembly: AvaloniaTestApplication(typeof(Runspace.Desktop.Tests.TestApplication))]

namespace Runspace.Desktop.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed partial class ConsoleTests
{
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10, timeout.Token);
        }
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTheory]
    [InlineData(ActionResultPolicy.Retain)]
    [InlineData(ActionResultPolicy.Refresh)]
    [InlineData(ActionResultPolicy.Replace)]
    [InlineData(ActionResultPolicy.Related)]
    public async Task DeclaredResultTransitionsKeepPartialOutcomeAndRelatedBackRoute(ActionResultPolicy policy)
    {
        var session = new FixtureSession { ExecutionOutcome = InvocationOutcome.CompletedWithErrors };
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
            await window.InvokeActionAsync(new(ConsoleActionId.ProcessModules, "Fixture transition", "General", "Fixture", true,
                ResultPolicy: policy));
            Assert.Equal(policy == ActionResultPolicy.Refresh ? 2 : 1, session.Queries);
            Assert.Contains("CompletedWithErrors", ((ConsoleViewModel)window.DataContext!).Status);
            Assert.Contains("partial-fixture-failure", ((ConsoleViewModel)window.DataContext!).Diagnostics);
            Assert.True(window.FindControl<Border>("ResultMessage")!.IsVisible);
            Assert.Equal(policy is ActionResultPolicy.Replace or ActionResultPolicy.Related ? "action-output" : "alpha",
                grid.ItemsSource!.Cast<ConsoleRow>().First().Cells["Name"].Value);
            if (policy == ActionResultPolicy.Related)
            {
                Assert.True(window.FindControl<Button>("BackButton")!.IsEnabled);
                Click(window.FindControl<Button>("BackButton")!);
                await UntilAsync(() => session.Queries == 2 && !((ConsoleViewModel)window.DataContext!).IsBusy);
                Assert.Equal("alpha", grid.ItemsSource!.Cast<ConsoleRow>().First().Cells["Name"].Value);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task StopDuringParameterPreparationDoesNotExecuteOrLeavePromptOpen()
    {
        var session = new FixtureSession();
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
            var action = window.InvokeActionAsync(new(ConsoleActionId.StopProcess, "Stop fixture", "General", "Fixture", true));
            Assert.Single(window.OwnedWindows);
            Assert.True(window.FindControl<Button>("StopButton")!.IsEnabled);
            Click(window.FindControl<Button>("StopButton")!);
            await action;
            Assert.Empty(window.OwnedWindows);
            Assert.Null(session.LastOutcome);
            Assert.Contains("Cancelled", ((ConsoleViewModel)window.DataContext!).Status);
            Assert.False(((ConsoleViewModel)window.DataContext!).IsBusy);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(InvocationOutcome.Failed)]
    [InlineData(InvocationOutcome.Cancelled)]
    public async Task RefreshAfterFailedOrCancelledMutationKeepsOriginalOutcome(InvocationOutcome outcome)
    {
        var session = new FixtureSession { ExecutionOutcome = outcome };
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
            await window.InvokeActionAsync(new(ConsoleActionId.ProcessModules, "Fixture mutation", "General", "Fixture", true,
                ResultPolicy: ActionResultPolicy.Refresh));
            Assert.Equal(2, session.Queries);
            Assert.Contains(outcome.ToString(), ((ConsoleViewModel)window.DataContext!).Status);
            Assert.Contains(outcome.ToString(), window.FindControl<TextBlock>("ResultMessageText")!.Text);
            Assert.Contains("partial-fixture-failure", ((ConsoleViewModel)window.DataContext!).Diagnostics);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SecureOutputSuppressionIsVisibleRatherThanAnEmptySuccess()
    {
        var window = new MainWindow(new FixtureSession { SuppressQueryOutput = true });
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            Assert.True(window.FindControl<Border>("ResultMessage")!.IsVisible);
            Assert.Contains("Output withheld", window.FindControl<TextBlock>("ResultMessageText")!.Text);
            Assert.True(window.FindControl<Border>("DiagnosticsPane")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HostFieldsValidateOffDispatcherAndChoiceAndSecretsRemainEditable()
    {
        var window = new Window();
        window.Show();
        try
        {
            var validationThread = false;
            var prompt = new HostPrompt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), HostPromptKind.Fields,
                "Typed input", "Fixture", [new("Count", "Count", "System.Int32", true)], [], -1,
                response => Task.Run(() =>
                {
                    validationThread = !Dispatcher.UIThread.CheckAccess();
                    return int.TryParse(response.Values["Count"]?.ToString(), out _) ? null : "Enter an integer.";
                }));
            var task = Dialogs.HostPromptAsync(window, prompt, CancellationToken.None);
            var dialog = window.OwnedWindows.Single();
            Click(Named<Button>(dialog, "PromptAccept"));
            Assert.Contains("required", Named<TextBlock>(dialog, "PromptError").Text);
            Named<TextBox>(dialog, "Count").Text = "bad";
            Click(Named<Button>(dialog, "PromptAccept"));
            await UntilAsync(() => Named<TextBlock>(dialog, "PromptError").Text == "Enter an integer.");
            Assert.False(task.IsCompleted);
            Named<TextBox>(dialog, "Count").Text = "42";
            Click(Named<Button>(dialog, "PromptAccept"));
            using var response = await task;
            Assert.Equal("42", response!.Values["Count"]);
            Assert.True(validationThread);

            var choicePrompt = prompt with { Kind = HostPromptKind.Choice, Fields = [], Choices = [new("&Yes", "Accept"), new("&No", "Decline")],
                DefaultChoice = 1, ValidateAsync = response => Task.FromResult<string?>(null) };
            var choiceTask = Dialogs.HostPromptAsync(window, choicePrompt, CancellationToken.None);
            dialog = window.OwnedWindows.Single();
            Assert.Equal(1, Named<ComboBox>(dialog, "PromptChoice").SelectedIndex);
            Click(Named<Button>(dialog, "PromptAccept"));
            using var choiceResponse = await choiceTask;
            Assert.Equal(1, choiceResponse!.Choice);

            var credentialPrompt = prompt with { Kind = HostPromptKind.Credential,
                Fields = [new("Credential", "Credential", "System.Management.Automation.PSCredential", true)],
                ValidateAsync = response => Task.FromResult<string?>(null) };
            var credentialTask = Dialogs.HostPromptAsync(window, credentialPrompt, CancellationToken.None);
            dialog = window.OwnedWindows.Single();
            var password = Named<TextBox>(dialog, "CredentialPassword");
            Assert.Equal('*', password.PasswordChar);
            Named<TextBox>(dialog, "Credential").Text = "fixture-user";
            password.Text = "fixture-password";
            Click(Named<Button>(dialog, "PromptAccept"));
            using var credentialResponse = await credentialTask;
            Assert.IsType<HostCredential>(credentialResponse!.Values["Credential"]);
            Assert.Equal(string.Empty, password.Text);

            var secureTask = Dialogs.HostPromptAsync(window, prompt with { Kind = HostPromptKind.SecureInput,
                Fields = [new("Value", "Secret", "System.Security.SecureString", true)],
                ValidateAsync = response => Task.FromResult<string?>(null) }, CancellationToken.None);
            dialog = window.OwnedWindows.Single();
            var secret = Named<TextBox>(dialog, "Value");
            secret.Text = "fixture-secret";
            Assert.Equal('*', secret.PasswordChar);
            Click(Named<Button>(dialog, "PromptAccept"));
            using var secureResponse = await secureTask;
            Assert.IsType<System.Security.SecureString>(secureResponse!.Values["Value"]);
            Assert.Equal(string.Empty, secret.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndApplicationCloseDismissHostPromptAndUnblockExecution(bool close)
    {
        var session = new FixtureSession { PromptDuringExecution = true };
        var window = new MainWindow(session);
        window.Show();
        await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
        var grid = window.FindControl<DataGrid>("ResultsGrid")!;
        grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
        var action = window.FindControl<StackPanel>("ActionsPanel")!.Children.OfType<Button>()
            .Single(button => Equals(button.Tag, ConsoleActionId.StopProcess));
        Click(action);
        var parameters = window.OwnedWindows.Single();
        Click(parameters.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "OK")));
        await UntilAsync(() => window.OwnedWindows.Any(dialog => dialog.Name == "InvocationPrompt"));
        Assert.True(window.IsEnabled);
        Assert.True(window.FindControl<Button>("StopButton")!.IsEnabled);
        if (close) window.Close();
        else Click(window.FindControl<Button>("StopButton")!);
        await UntilAsync(() => session.LastOutcome == InvocationOutcome.Cancelled);
        Assert.Empty(window.OwnedWindows);
        if (close) await UntilAsync(() => session.Disposed);
        else
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            Assert.Contains("Cancelled", ((ConsoleViewModel)window.DataContext!).Status);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SelectionIsFrozenBeforeParametersAndPartialFailureSurvivesRefresh()
    {
        var session = new FixtureSession { ExecutionOutcome = InvocationOutcome.CompletedWithErrors };
        var window = new MainWindow(session);
        window.Show();
        await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
        var grid = window.FindControl<DataGrid>("ResultsGrid")!;
        var rows = grid.ItemsSource!.Cast<ConsoleRow>().ToArray();
        grid.SelectedItem = rows[0];
        Click(window.FindControl<StackPanel>("ActionsPanel")!.Children.OfType<Button>()
            .Single(button => Equals(button.Tag, ConsoleActionId.StopProcess)));
        var parameters = window.OwnedWindows.Single();
        grid.SelectedItem = rows[1];
        Click(parameters.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "OK")));
        await UntilAsync(() => session.LastOutcome is not null && !((ConsoleViewModel)window.DataContext!).IsBusy);
        Assert.Equal([rows[0].Handle], session.LastSelection);
        Assert.Equal(2, session.Queries);
        var model = (ConsoleViewModel)window.DataContext!;
        Assert.Contains("CompletedWithErrors", model.Status);
        Assert.Contains("partial-fixture-failure", model.Diagnostics);
        Assert.True(window.FindControl<Border>("ResultMessage")!.IsVisible);
        window.Close();
    }

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
    public async Task ProviderContainersLoadOnlyOnExpansionAndReleaseTemporaryResults()
    {
        var root = new ConsoleNode("literal-drive", "Literal:", ResourceKind.ProviderPath, "Session drive", "Literal:\\", ProviderName: "FileSystem");
        var child = new ConsoleNode("literal-child", "quoted'[one]*?", ResourceKind.ProviderPath, "Literal container",
            "Literal:\\quoted'[one]*?", ProviderName: "FileSystem");
        var pending = new TaskCompletionSource<ConsoleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = ProviderResult(
            new(Guid.NewGuid(), new Dictionary<string, ConsoleCell> { ["Name"] = ConsoleCell.From(child.Name) }, child),
            new(Guid.NewGuid(), new Dictionary<string, ConsoleCell> { ["Name"] = ConsoleCell.From("leaf.txt") }));
        var empty = ProviderResult();
        var session = new FixtureSession
        {
            DriveNodes = [root],
            Query = node => node == root ? pending.Task : Task.FromResult(node == child ? empty : ProviderResult())
        };
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.UpdateLayout();
            var drive = Assert.Single(((ConsoleViewModel)window.DataContext!).Roots[2].Children);
            Assert.True(drive.IsLazy);
            Assert.Equal("Expand to load...", Assert.Single(drive.Children).Name);
            Assert.DoesNotContain(session.QueryNodes, node => node.Kind == ResourceKind.ProviderPath);
            var container = window.FindControl<TreeView>("NavigationTree")!.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(item => ReferenceEquals(item.DataContext, drive));
            container.IsExpanded = true;
            await UntilAsync(() => drive.IsLoading);
            container.RaiseEvent(new RoutedEventArgs(TreeViewItem.ExpandedEvent));
            Assert.Single(session.QueryNodes, node => node == root);
            Assert.DoesNotContain(session.QueryNodes, node => node == child);
            pending.SetResult(result);
            await UntilAsync(() => !drive.IsLoading);
            Assert.False(drive.IsLazy);
            var nested = Assert.Single(drive.Children);
            Assert.Equal(child, nested.Node);
            Assert.True(nested.IsLazy);
            Assert.Contains(result.Id, session.Released);
            container.IsExpanded = false;
            container.IsExpanded = true;
            Assert.Single(session.QueryNodes, node => node == root);
            window.UpdateLayout();
            var childContainer = window.FindControl<TreeView>("NavigationTree")!.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(item => ReferenceEquals(item.DataContext, nested));
            childContainer.IsExpanded = true;
            await UntilAsync(() => !nested.IsLazy);
            Assert.Single(session.QueryNodes, node => node == child);
            Assert.Empty(nested.Children);
            Assert.Contains(empty.Id, session.Released);
        }
        finally { pending.TrySetResult(result); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Environment", "Env:\\")]
    [InlineData("Alias", "Alias:\\")]
    [InlineData("Variable", "Variable:\\")]
    [InlineData("Function", "Function:\\")]
    public async Task FlatProvidersShowItemsWithoutInventingContainerOrMutationActions(string provider, string path)
    {
        var node = new ConsoleNode("flat-provider", provider, ResourceKind.ProviderPath, "Flat provider", path, ProviderName: provider);
        var session = new FixtureSession
        {
            DriveNodes = [node],
            Query = _ => Task.FromResult(ProviderResult(
                new ConsoleRow(Guid.NewGuid(), new Dictionary<string, ConsoleCell> { ["Name"] = ConsoleCell.From("leaf") })))
        };
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.UpdateLayout();
            var tree = window.FindControl<TreeView>("NavigationTree")!;
            var drive = Assert.Single(((ConsoleViewModel)window.DataContext!).Roots[2].Children);
            var container = tree.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(item => ReferenceEquals(item.DataContext, drive));
            container.IsExpanded = true;
            await UntilAsync(() => !drive.IsLazy);
            Assert.Empty(drive.Children);
            tree.SelectedItem = drive;
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItem = Assert.Single(grid.ItemsSource!.Cast<ConsoleRow>());
            var actions = window.FindControl<StackPanel>("ActionsPanel")!.Children.OfType<Button>().ToArray();
            Assert.True(actions.Single(button => Equals(button.Tag, ConsoleActionId.Properties)).IsEnabled);
            Assert.DoesNotContain(actions, button => button.Tag is ConsoleActionId.Browse or ConsoleActionId.SetValue
                or ConsoleActionId.RemoveItem or ConsoleActionId.AddDrive or ConsoleActionId.RemoveDrive);
            Assert.False(window.FindControl<Border>("ResultMessage")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(InvocationOutcome.Failed, false)]
    [InlineData(InvocationOutcome.CompletedWithErrors, false)]
    [InlineData(InvocationOutcome.CompletedWithErrors, true)]
    public async Task FailedOrPartialProviderExpansionIsVisibleAndCanBeRetried(InvocationOutcome outcome, bool hasChildren)
    {
        var root = new ConsoleNode("provider-root", "Provider:", ResourceKind.ProviderPath, "Provider navigation", "Provider:\\");
        var child = new ConsoleNode("usable-child", "Usable child", ResourceKind.ProviderPath, "Usable container", "Provider:\\child");
        var complete = ProviderResult(new ConsoleRow(Guid.NewGuid(),
            new Dictionary<string, ConsoleCell> { ["Name"] = ConsoleCell.From(child.Name) }, child));
        var incomplete = (hasChildren ? complete with { Id = Guid.NewGuid() } : ProviderResult()) with
        {
            Outcome = outcome,
            Diagnostics = [new(DateTimeOffset.Now, "Error", "fixture-container-navigation-unsupported")]
        };
        var attempts = 0;
        var session = new FixtureSession
        {
            DriveNodes = [root],
            Query = node => Task.FromResult(node == root ? ++attempts == 1 ? incomplete : complete : ProviderResult())
        };
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.UpdateLayout();
            var model = (ConsoleViewModel)window.DataContext!;
            var drive = Assert.Single(model.Roots[2].Children);
            var previousMessage = window.FindControl<TextBlock>("ResultMessageText")!.Text;
            var previousStatus = model.Status;
            var container = window.FindControl<TreeView>("NavigationTree")!.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(item => ReferenceEquals(item.DataContext, drive));
            container.IsExpanded = true;
            await UntilAsync(() => attempts == 1 && !drive.IsLoading);
            Assert.True(drive.IsLazy);
            Assert.Contains("fixture-container-navigation-unsupported", model.Diagnostics);
            Assert.True(window.FindControl<Border>("DiagnosticsPane")!.IsVisible);
            Assert.True(window.FindControl<Border>("ResultMessage")!.IsVisible);
            if (outcome == InvocationOutcome.CompletedWithErrors)
            {
                if (hasChildren) Assert.Equal(child, Assert.Single(drive.Children).Node);
                else Assert.Equal("Expand to retry...", Assert.Single(drive.Children).Name);
                Assert.Contains("incomplete", window.FindControl<TextBlock>("ResultMessageText")!.Text);
            }
            else Assert.Equal("Expand to load...", Assert.Single(drive.Children).Name);
            Assert.Contains(incomplete.Id, session.Released);
            container.IsExpanded = false;
            container.IsExpanded = true;
            await UntilAsync(() => attempts == 2 && !drive.IsLoading);
            Assert.False(drive.IsLazy);
            Assert.Equal(child, Assert.Single(drive.Children).Node);
            Assert.Contains(complete.Id, session.Released);
            if (outcome == InvocationOutcome.CompletedWithErrors)
            {
                Assert.Equal(previousMessage, window.FindControl<TextBlock>("ResultMessageText")!.Text);
                Assert.Equal(previousStatus, model.Status);
            }
        }
        finally { window.Close(); }
    }

    private static ConsoleResult ProviderResult(params ConsoleRow[] rows) =>
        new(Guid.NewGuid(), [new("Name", "Name")], rows, "Get-ChildItem -LiteralPath '<fixture>'", [],
            TimeSpan.Zero, InvocationOutcome.Completed);

    [AvaloniaTheory]
    [InlineData(ConsoleActionId.AddDrive, InvocationOutcome.Completed)]
    [InlineData(ConsoleActionId.AddDrive, InvocationOutcome.Failed)]
    [InlineData(ConsoleActionId.RemoveDrive, InvocationOutcome.Completed)]
    [InlineData(ConsoleActionId.RemoveDrive, InvocationOutcome.Failed)]
    public async Task DriveActionsRefreshNavigationWithoutRestartAndKeepFailedOutcomes(ConsoleActionId actionId, InvocationOutcome outcome)
    {
        var name = "Fixture" + Guid.NewGuid().ToString("N");
        var drive = new ConsoleNode("fixture-drive", name, ResourceKind.ProviderPath, "Fixture drive", $"{name}:\\", ProviderName: "FileSystem");
        List<ConsoleNode> drives = actionId == ConsoleActionId.RemoveDrive ? [drive] : [];
        var session = new FixtureSession
        {
            DriveNodes = drives,
            ExecutionOutcome = outcome,
            Executing = _ =>
            {
                if (outcome != InvocationOutcome.Completed) return;
                if (actionId == ConsoleActionId.AddDrive) drives.Add(drive);
                else drives.Clear();
            },
            Query = node => Task.FromResult(node.Kind == ResourceKind.Drives
                ? ProviderResult(drives.Select(driveNode => new ConsoleRow(Guid.NewGuid(),
                    new Dictionary<string, ConsoleCell> { ["Name"] = ConsoleCell.From(driveNode.Name) }, driveNode)).ToArray())
                : ProviderResult())
        };
        var window = new MainWindow(session);
        window.Show();
        try
        {
            var model = (ConsoleViewModel)window.DataContext!;
            await UntilAsync(() => !model.IsBusy);
            window.FindControl<TreeView>("NavigationTree")!.SelectedItem =
                model.Roots[0].Children.Single(item => item.Node.Kind == ResourceKind.Drives);
            await UntilAsync(() => !model.IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            if (actionId == ConsoleActionId.RemoveDrive) grid.SelectedItem = Assert.Single(grid.ItemsSource!.Cast<ConsoleRow>());
            var action = BuiltInCatalog.GetActions(BuiltInCatalog.LocalSystem.Single(node => node.Kind == ResourceKind.Drives),
                grid.SelectedItems.Cast<ConsoleRow>().ToArray()).Single(action => action.Id == actionId);
            var operation = window.InvokeActionAsync(action);
            await UntilAsync(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            if (actionId == ConsoleActionId.AddDrive)
            {
                var inputs = dialog.GetLogicalDescendants().OfType<TextBox>().ToArray();
                inputs[0].Text = name;
                inputs[1].Text = "fixture-root'[one]*?;$(not-code)";
            }
            Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "OK")));
            await operation;
            Assert.Equal(2, session.DriveReads);
            Assert.Equal(drives, model.Roots[2].Children.Select(item => item.Node));
            Assert.Equal(drives.Select(node => node.Name), grid.ItemsSource!.Cast<ConsoleRow>().Select(row => row.Cells["Name"].Display));
            Assert.Contains(outcome.ToString(), model.Status);
            Assert.Equal(2, session.QueryNodes.Count(node => node.Kind == ResourceKind.Drives));
            if (outcome == InvocationOutcome.Failed)
            {
                Assert.True(window.FindControl<Border>("ResultMessage")!.IsVisible);
                Assert.Contains("Failed", window.FindControl<TextBlock>("ResultMessageText")!.Text);
                Assert.Contains("partial-fixture-failure", model.Diagnostics);
            }
            if (actionId == ConsoleActionId.AddDrive)
            {
                Assert.Empty(session.LastSelection!);
                Assert.Equal(name, session.LastParameters!["Name"]);
                Assert.Equal("FileSystem", session.LastParameters["Provider"]);
                Assert.Equal("fixture-root'[one]*?;$(not-code)", session.LastParameters["Root"]);
            }
            else Assert.Single(session.LastSelection!);
        }
        finally { window.Close(); }
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

    private sealed class FixtureSession : IConsoleSession, IInvocationHostSession
    {
        public Func<HostPrompt, CancellationToken, Task<HostResponse?>>? PromptHandler { get; set; }
        public event Action<InvocationStatus>? StateChanged;
        public bool StopInvocation(Guid invocationId) => false;
        public bool PromptDuringExecution { get; init; }
        public bool SuppressQueryOutput { get; init; }
        public InvocationOutcome ExecutionOutcome { get; init; } = InvocationOutcome.Completed;
        public InvocationOutcome? LastOutcome { get; private set; }
        public IReadOnlyList<Guid>? LastSelection { get; private set; }
        public IReadOnlyDictionary<string, string>? LastParameters { get; private set; }
        public Action<ConsoleActionId>? Executing { get; init; }
        public int Queries { get; private set; }
        public int DriveReads { get; private set; }
        public List<ConsoleNode> QueryNodes { get; } = [];
        public List<Guid> Released { get; } = [];
        public Func<ConsoleNode, Task<ConsoleResult>>? Query { get; init; }
        public IReadOnlyList<ConsoleNode> DriveNodes { get; init; } =
            [new("env", "Env:", ResourceKind.ProviderPath, "Environment", "Env:")];
        public bool Disposed { get; private set; }
        public string RuntimeVersion => "fixture";
        public Task<ConsoleResult> QueryAsync(ConsoleNode node, CancellationToken cancellationToken = default)
        {
            Queries++;
            QueryNodes.Add(node);
            if (Query is not null) return Query(node);
            ConsoleColumn[] columns = [new("Name", "Name"), new("Id", "Id", ColumnKind.Number)];
            ConsoleRow[] rows = [Row("alpha", 100), Row("beta", 2)];
            return Task.FromResult(new ConsoleResult(Guid.NewGuid(), columns, SuppressQueryOutput ? [] : rows, "Get-Process", [],
                TimeSpan.Zero, InvocationOutcome.Completed, OutputSuppressed: SuppressQueryOutput));
        }
        private static ConsoleRow Row(string name, int id) => new(Guid.NewGuid(), new Dictionary<string, ConsoleCell>
        {
            ["Name"] = ConsoleCell.From(name), ["Id"] = ConsoleCell.From(id)
        });
        public async Task<ConsoleResult> ExecuteAsync(ConsoleActionId action, Guid resultId, IReadOnlyList<Guid> selection,
            IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            LastSelection = selection.ToArray();
            LastParameters = new Dictionary<string, string>(parameters);
            Executing?.Invoke(action);
            var id = Guid.NewGuid();
            var outcome = ExecutionOutcome;
            if (PromptDuringExecution)
            {
                StateChanged?.Invoke(new(Guid.Empty, id, InvocationState.AwaitingInput));
                try
                {
                    using var response = await PromptHandler!(new(Guid.Empty, id, Guid.NewGuid(), HostPromptKind.Input,
                        "Fixture host input", "Waiting", [new("Value", "Value", "System.String", true)], [], -1,
                        response => Task.FromResult<string?>(null)), cancellationToken);
                    if (response is null || cancellationToken.IsCancellationRequested) outcome = InvocationOutcome.Cancelled;
                }
                catch (OperationCanceledException) { outcome = InvocationOutcome.Cancelled; }
            }
            LastOutcome = outcome;
            return new(id, [new("Name", "Name")], [Row("action-output", 123)], "Fixture invocation",
                [new(DateTimeOffset.Now, "Error", "partial-fixture-failure")], TimeSpan.Zero, outcome);
        }
        public Task<IReadOnlyList<ObjectProperty>> InspectAsync(Guid resultId, Guid handle, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ObjectProperty>>([new("Name", "string", "fixture")]);
        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderInfo>>([new("Environment", "ShouldProcess"), new("FileSystem", "ShouldProcess")]);
        public Task<IReadOnlyList<ConsoleNode>> GetDriveNodesAsync(CancellationToken cancellationToken = default)
        {
            DriveReads++;
            return Task.FromResult<IReadOnlyList<ConsoleNode>>(DriveNodes.ToArray());
        }
        public void ReleaseResult(Guid resultId) => Released.Add(resultId);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
