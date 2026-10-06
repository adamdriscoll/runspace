using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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
        public int Queries { get; private set; }
        public bool Disposed { get; private set; }
        public string RuntimeVersion => "fixture";
        public Task<ConsoleResult> QueryAsync(ConsoleNode node, CancellationToken cancellationToken = default)
        {
            Queries++;
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
            Task.FromResult<IReadOnlyList<ProviderInfo>>([new("Environment", "ShouldProcess")]);
        public Task<IReadOnlyList<ConsoleNode>> GetDriveNodesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConsoleNode>>([new("env", "Env:", ResourceKind.ProviderPath, "Environment", "Env:")]);
        public void ReleaseResult(Guid resultId) { }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
