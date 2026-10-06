using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Runspace.Core;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;
using Runspace.PowerShell;

namespace Runspace.Desktop.Tests;

public sealed class InvocationLifecycleTests
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

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static ConsoleViewModel Model(Window window) => (ConsoleViewModel)window.DataContext!;
    private static DataGrid Grid(Window window) => window.FindControl<DataGrid>("ResultsGrid")!;
    private static void Navigate(Window window, ResourceKind kind) =>
        window.FindControl<TreeView>("NavigationTree")!.SelectedItem =
            Model(window).Roots[0].Children.Single(item => item.Node.Kind == kind);

    private static ConsoleResult Result(string name, InvocationOutcome outcome = InvocationOutcome.Completed, bool empty = false) =>
        new(Guid.NewGuid(), [new("Name", "Name"), new("Id", "Id", ColumnKind.Number)],
            empty ? [] : [new(Guid.NewGuid(), new Dictionary<string, ConsoleCell>
            {
                ["Name"] = ConsoleCell.From(name), ["Id"] = ConsoleCell.From(42)
            })], "Fixture command -Value '<REDACTED>'",
            outcome == InvocationOutcome.Completed ? [] : [new(DateTimeOffset.Now, "Error", "fixture-error")],
            TimeSpan.Zero, outcome);

    [AvaloniaTheory]
    [InlineData(InvocationOutcome.Completed)]
    [InlineData(InvocationOutcome.CompletedWithErrors)]
    [InlineData(InvocationOutcome.Failed)]
    public async Task QueryDistinguishesEmptyPartialAndIncompleteResults(InvocationOutcome outcome)
    {
        var session = new LifecycleSession
        {
            Query = _ => Task.FromResult(Result("usable", outcome, empty: outcome == InvocationOutcome.Completed))
        };
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => Model(window).Status.StartsWith(outcome.ToString()));
            Assert.False(Model(window).IsBusy);
            var message = window.FindControl<TextBlock>("ResultMessageText")!.Text;
            if (outcome == InvocationOutcome.Completed)
            {
                Assert.Empty(Grid(window).ItemsSource!.Cast<ConsoleRow>());
                Assert.Equal("No objects were returned.", message);
                Assert.False(window.FindControl<Border>("DiagnosticsPane")!.IsVisible);
                Assert.DoesNotContain("Error", Model(window).Status);
            }
            else
            {
                var row = Assert.Single(Grid(window).ItemsSource!.Cast<ConsoleRow>());
                Assert.Equal("usable", row.Cells["Name"].Value);
                Grid(window).SelectedItem = row;
                Assert.True(window.FindControl<StackPanel>("ActionsPanel")!.Children.OfType<Button>()
                    .Single(button => Equals(button.Tag, ConsoleActionId.Properties)).IsEnabled);
                Assert.Contains("1 error", message);
                if (outcome == InvocationOutcome.Failed) Assert.Contains("incomplete", message);
                Assert.Contains("fixture-error", Model(window).Diagnostics);
            }
        }
        finally { window.Close(); await UntilAsync(() => session.Disposed); }
    }

    [AvaloniaFact]
    public async Task SupersededNavigationCannotReplaceRowsActionsPromptsOrStatus()
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        var first = new TaskCompletionSource<ConsoleResult>();
        var second = new TaskCompletionSource<ConsoleResult>();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            var initialId = Assert.Single(session.LiveResults);
            session.Query = request => request.Node.Kind == ResourceKind.Environment ? first.Task : second.Task;
            Navigate(window, ResourceKind.Environment);
            var oldRequest = session.Queries[^1];
            Navigate(window, ResourceKind.Providers);
            Assert.True(oldRequest.Token.IsCancellationRequested);
            var loading = Model(window).Status;
            var prompt = new HostPrompt(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), HostPromptKind.Input,
                "Old query", "Must not open", [new("Value", "Value", "System.String", true)], [], -1,
                _ => Task.FromResult<string?>(null));
            var response = oldRequest.Prompt!(prompt, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Empty(window.OwnedWindows);
            Assert.Null(await response);
            session.Notify(prompt.InvocationId, InvocationState.AwaitingInput);
            session.Notify(prompt.InvocationId, InvocationState.Unresponsive);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(loading, Model(window).Status);

            var current = Result("new-provider");
            second.SetResult(current);
            await UntilAsync(() => !Model(window).IsBusy);
            var status = Model(window).Status;
            var stale = Result("old-environment", InvocationOutcome.Failed);
            first.SetResult(stale);
            await UntilAsync(() => session.Released.Contains(stale.Id));
            Assert.Equal("new-provider", Assert.Single(Grid(window).ItemsSource!.Cast<ConsoleRow>()).Cells["Name"].Value);
            Assert.Equal(status, Model(window).Status);
            Assert.False(window.FindControl<Border>("ResultMessage")!.IsVisible);
            Assert.DoesNotContain(window.FindControl<StackPanel>("ActionsPanel")!.Children.OfType<Button>(),
                button => Equals(button.Tag, ConsoleActionId.SetValue));
            Assert.Equal([current.Id], session.LiveResults);
            Assert.Contains(initialId, session.Released);
            Assert.Contains("Environment", Model(window).History);
            Assert.Contains("Failed", Model(window).History);
        }
        finally
        {
            first.TrySetResult(Result("cleanup"));
            second.TrySetResult(Result("cleanup"));
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    [AvaloniaFact]
    public async Task NavigationDuringActionRefreshCannotRestoreOldFailureBannerOrStatus()
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        var refresh = new TaskCompletionSource<ConsoleResult>();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            Grid(window).SelectedItem = Grid(window).ItemsSource!.Cast<ConsoleRow>().Single();
            session.Query = request => request.Node.Kind == ResourceKind.Processes
                ? refresh.Task : Task.FromResult(Result("new-view"));
            var actionResult = Result("action-output", InvocationOutcome.CompletedWithErrors);
            session.Execute = () => Task.FromResult(actionResult);
            var action = window.InvokeActionAsync(new(ConsoleActionId.ProcessModules, "Partial action", "General",
                "Fixture", true, ResultPolicy: ActionResultPolicy.Refresh));
            await UntilAsync(() => session.Queries.Count == 2);
            Navigate(window, ResourceKind.Environment);
            await UntilAsync(() => !Model(window).IsBusy);
            var status = Model(window).Status;
            var oldRefresh = Result("old-refresh");
            refresh.SetResult(oldRefresh);
            await action;
            Assert.Equal(status, Model(window).Status);
            Assert.False(window.FindControl<Border>("ResultMessage")!.IsVisible);
            Assert.Equal("new-view", Assert.Single(Grid(window).ItemsSource!.Cast<ConsoleRow>()).Cells["Name"].Value);
            Assert.Contains(actionResult.Id, session.Released);
            Assert.Contains(oldRefresh.Id, session.Released);
            Assert.Contains("CompletedWithErrors", Model(window).History);
            Assert.Contains("fixture-error", Model(window).Diagnostics);
        }
        finally
        {
            refresh.TrySetResult(Result("cleanup"));
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    [AvaloniaFact]
    public async Task SupersededLifecycleNotificationCannotClaimEvenAStoppedNewRequest()
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        var first = new TaskCompletionSource<ConsoleResult>();
        var second = new TaskCompletionSource<ConsoleResult>();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            session.Query = request => request.Node.Kind == ResourceKind.Environment ? first.Task : second.Task;
            Navigate(window, ResourceKind.Environment);
            Navigate(window, ResourceKind.Providers);
            Click(window.FindControl<Button>("StopButton")!);
            session.Notify(Guid.NewGuid(), InvocationState.Unresponsive);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal("Stopping...", Model(window).Status);
            await UntilAsync(() => Model(window).Status.Contains("unresponsive"));
            Assert.True(Model(window).IsBusy);
            second.SetResult(Result("cancelled-new-view", InvocationOutcome.Cancelled));
            await UntilAsync(() => !Model(window).IsBusy);
            var status = Model(window).Status;
            first.SetResult(Result("old-view", InvocationOutcome.Cancelled));
            await UntilAsync(() => session.Released.Count == 2);
            Assert.Equal(status, Model(window).Status);
        }
        finally
        {
            first.TrySetResult(Result("cleanup"));
            second.TrySetResult(Result("cleanup"));
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededQueryExceptionsAreDiagnosticWithoutReplacingNewViewStatus(bool cancelled)
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        var completion = new TaskCompletionSource<ConsoleResult>();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            session.Query = request => request.Node.Kind == ResourceKind.Environment
                ? completion.Task : Task.FromResult(Result("new-view"));
            Navigate(window, ResourceKind.Environment);
            Navigate(window, ResourceKind.Providers);
            var status = Model(window).Status;
            completion.SetException(cancelled
                ? new OperationCanceledException("late-query-cancellation")
                : new InvalidOperationException("late-query-failure"));
            await UntilAsync(() => session.FinishedQueries == 3);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(status, Model(window).Status);
            Assert.False(Model(window).IsBusy);
            Assert.False(window.FindControl<Border>("ResultMessage")!.IsVisible);
            Assert.Equal("new-view", Assert.Single(Grid(window).ItemsSource!.Cast<ConsoleRow>()).Cells["Name"].Value);
            if (!cancelled) Assert.Contains("late-query-failure", Model(window).Diagnostics);
        }
        finally
        {
            completion.TrySetResult(Result("cleanup"));
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededActionOrInspectionFailureStaysDiagnosticWithoutClaimingNewView(bool inspect)
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        var actionOutput = new TaskCompletionSource<ConsoleResult>();
        var properties = new TaskCompletionSource<IReadOnlyList<ObjectProperty>>();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            Grid(window).SelectedItem = Grid(window).ItemsSource!.Cast<ConsoleRow>().Single();
            session.Execute = () => actionOutput.Task;
            session.Inspect = _ => properties.Task;
            var operation = window.InvokeActionAsync(new(inspect ? ConsoleActionId.Properties : ConsoleActionId.ProcessModules,
                "Old operation", "General", "Fixture", true, ResultPolicy: ActionResultPolicy.Retain));
            Navigate(window, ResourceKind.Environment);
            var status = Model(window).Status;
            if (inspect) properties.SetException(new InvalidOperationException("late-operation-failure"));
            else actionOutput.SetException(new InvalidOperationException("late-operation-failure"));
            await operation;
            Assert.Equal(status, Model(window).Status);
            Assert.False(Model(window).IsBusy);
            Assert.False(window.FindControl<Border>("ResultMessage")!.IsVisible);
            Assert.Empty(window.OwnedWindows);
            Assert.Contains("late-operation-failure", Model(window).Diagnostics);
            Assert.Single(session.LiveResults);
        }
        finally
        {
            actionOutput.TrySetResult(Result("cleanup"));
            properties.TrySetResult([]);
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    [AvaloniaFact]
    public async Task ClosingReleasesCurrentAndLateQueryResultsRatherThanKeepingThemInHistory()
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        var completion = new TaskCompletionSource<ConsoleResult>();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            var current = Assert.Single(session.LiveResults);
            session.Query = _ => completion.Task;
            Navigate(window, ResourceKind.Environment);
            var request = session.Queries[^1];
            window.Close();
            await UntilAsync(() => session.Disposed);
            Assert.True(request.Token.IsCancellationRequested);
            Assert.Contains(current, session.Released);
            var status = Model(window).Status;
            var late = Result("late-live-object");
            completion.SetResult(late);
            await UntilAsync(() => session.Released.Contains(late.Id));
            Assert.Empty(session.LiveResults);
            Assert.Equal(status, Model(window).Status);
            Assert.DoesNotContain("late-live-object", Model(window).History);
            Assert.Equal(2, session.Released.Count);
        }
        finally
        {
            completion.TrySetResult(Result("cleanup"));
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedQueryOrInspectionDoesNotRemainInAPendingStateAfterCompletion(bool inspect)
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        var query = new TaskCompletionSource<ConsoleResult>();
        var properties = new TaskCompletionSource<IReadOnlyList<ObjectProperty>>();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            Task operation;
            if (inspect)
            {
                Grid(window).SelectedItem = Grid(window).ItemsSource!.Cast<ConsoleRow>().Single();
                session.Inspect = _ => properties.Task;
                operation = window.InvokeActionAsync(new(ConsoleActionId.Properties, "Properties", "General", "Fixture", true));
            }
            else
            {
                session.Query = _ => query.Task;
                Navigate(window, ResourceKind.Environment);
                operation = Task.CompletedTask;
            }
            Click(window.FindControl<Button>("StopButton")!);
            if (inspect) properties.SetResult([new("Value", "string", "late value")]);
            else query.SetException(new OperationCanceledException());
            await operation;
            await UntilAsync(() => !Model(window).IsBusy);
            Assert.Contains("cancelled", Model(window).Status, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(window.OwnedWindows);
            if (!inspect) Assert.DoesNotContain("pending", window.FindControl<TextBlock>("ResultMessageText")!.Text);
        }
        finally
        {
            query.TrySetResult(Result("cleanup"));
            properties.TrySetResult([]);
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingInspectionIsVisibleAndCannotOpenAfterNavigationOrClose(bool close)
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        var properties = new TaskCompletionSource<IReadOnlyList<ObjectProperty>>();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            Grid(window).SelectedItem = Grid(window).ItemsSource!.Cast<ConsoleRow>().Single();
            session.Inspect = _ => properties.Task;
            var inspection = window.InvokeActionAsync(new(ConsoleActionId.Properties, "Properties", "General", "Inspect", true));
            Assert.True(Model(window).IsBusy);
            Assert.Contains("Inspecting", Model(window).Status);
            Assert.False(inspection.IsCompleted);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (close) window.Close();
            else Navigate(window, ResourceKind.Environment);
            Assert.True(session.InspectionToken.IsCancellationRequested);
            var status = Model(window).Status;
            properties.SetResult([new("Broken", "Fixture", "(unavailable)", "getter-error")]);
            await inspection;
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(status, Model(window).Status);
        }
        finally
        {
            properties.TrySetResult([]);
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    [AvaloniaFact]
    public async Task NavigationDismissesPreparedActionWithoutOverwritingNewStatus()
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            Grid(window).SelectedItem = Grid(window).ItemsSource!.Cast<ConsoleRow>().Single();
            var action = window.InvokeActionAsync(new(ConsoleActionId.StopProcess, "Old action", "General", "Fixture", true));
            Assert.Single(window.OwnedWindows);
            Navigate(window, ResourceKind.Environment);
            var status = Model(window).Status;
            await action;
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(status, Model(window).Status);
        }
        finally { window.Close(); await UntilAsync(() => session.Disposed); }
    }

    [AvaloniaTheory]
    [InlineData(ActionResultPolicy.Retain)]
    [InlineData(ActionResultPolicy.Refresh)]
    [InlineData(ActionResultPolicy.Replace)]
    [InlineData(ActionResultPolicy.Related)]
    public async Task ResultPoliciesReleaseReplacedViewsAndBackRequeriesRatherThanRetainingObjects(ActionResultPolicy policy)
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            var sourceId = Assert.Single(session.LiveResults);
            Grid(window).SelectedItem = Grid(window).ItemsSource!.Cast<ConsoleRow>().Single();
            var output = Result("live-output");
            session.Execute = () => Task.FromResult(output);
            await window.InvokeActionAsync(new(ConsoleActionId.ProcessModules, "Related fixture", "General", "Fixture",
                true, ResultPolicy: policy));
            var retained = Assert.Single(session.LiveResults);
            Assert.Equal(policy == ActionResultPolicy.Retain, retained == sourceId);
            Assert.Equal(policy is ActionResultPolicy.Replace or ActionResultPolicy.Related, retained == output.Id);
            Assert.Equal(policy != ActionResultPolicy.Retain, session.Released.Contains(sourceId));
            Assert.Equal(policy is ActionResultPolicy.Retain or ActionResultPolicy.Refresh, session.Released.Contains(output.Id));
            var row = Grid(window).ItemsSource!.Cast<ConsoleRow>().Single();
            Assert.NotEmpty(await session.InspectAsync(retained, row.Handle));
            Assert.DoesNotContain("live-output", Model(window).History);
            Assert.Contains("<REDACTED>", Model(window).History);
            if (policy == ActionResultPolicy.Related)
            {
                Click(window.FindControl<Button>("BackButton")!);
                await UntilAsync(() => session.Queries.Count == 2 && !Model(window).IsBusy);
                Assert.Contains(retained, session.Released);
                Assert.NotEqual(sourceId, Assert.Single(session.LiveResults));
                Assert.NotEqual(output.Id, Assert.Single(session.LiveResults));
            }
            // An overview has no live result set and evicts the current object graph.
            window.FindControl<TreeView>("NavigationTree")!.SelectedItem = Model(window).Roots[0];
            Assert.Empty(session.LiveResults);
            window.Close();
            await UntilAsync(() => session.Disposed);
            Assert.Empty(session.LiveResults);
            Assert.Equal(session.Released.Count, session.Released.Distinct().Count());
        }
        finally { window.Close(); await UntilAsync(() => session.Disposed); }
    }

    [AvaloniaTheory]
    [InlineData(ActionResultPolicy.Retain)]
    [InlineData(ActionResultPolicy.Refresh)]
    [InlineData(ActionResultPolicy.Replace)]
    [InlineData(ActionResultPolicy.Related)]
    public async Task SupersededActionOutputIsReleasedWithoutChangingNewView(ActionResultPolicy policy)
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        var completion = new TaskCompletionSource<ConsoleResult>();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            Grid(window).SelectedItem = Grid(window).ItemsSource!.Cast<ConsoleRow>().Single();
            session.Execute = () => completion.Task;
            var action = window.InvokeActionAsync(new(ConsoleActionId.ProcessModules, "Old action", "General", "Fixture",
                true, ResultPolicy: policy));
            session.Query = _ => Task.FromResult(Result("new-view"));
            Navigate(window, ResourceKind.Environment);
            var status = Model(window).Status;
            var currentId = Assert.Single(session.LiveResults);
            var late = Result("old-action", InvocationOutcome.Failed);
            completion.SetResult(late);
            await action;
            Assert.Equal([currentId], session.LiveResults);
            Assert.Contains(late.Id, session.Released);
            Assert.Equal(status, Model(window).Status);
            Assert.Equal("new-view", Grid(window).ItemsSource!.Cast<ConsoleRow>().Single().Cells["Name"].Value);
            Assert.False(window.FindControl<Border>("ResultMessage")!.IsVisible);
            Assert.False(Model(window).IsBusy);
        }
        finally
        {
            completion.TrySetResult(Result("cleanup"));
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    [AvaloniaFact]
    public async Task BoundedHistoryRetainsRedactedDescriptionsAndOutcomesButNoActionResults()
    {
        var session = new LifecycleSession();
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await UntilAsync(() => !Model(window).IsBusy);
            var source = Assert.Single(session.LiveResults);
            Grid(window).SelectedItem = Grid(window).ItemsSource!.Cast<ConsoleRow>().Single();
            var number = 0;
            session.Execute = () => Task.FromResult(Result("live-only-secret") with
            {
                Script = $"Safe invocation {++number} -Value '<REDACTED>'"
            });
            for (var index = 0; index < 205; index++)
                await window.InvokeActionAsync(new(ConsoleActionId.ProcessModules, "Retained action", "General", "Fixture",
                    true, ResultPolicy: ActionResultPolicy.Retain));
            Assert.Equal([source], session.LiveResults);
            Assert.Equal(205, session.Released.Count);
            Assert.Equal(200, Model(window).History.Split("| Retained action |").Length - 1);
            Assert.Contains("latest 200", Model(window).History);
            Assert.Contains("Safe invocation 6 -", Model(window).History);
            Assert.Contains("Safe invocation 205 -", Model(window).History);
            Assert.DoesNotContain("Safe invocation 5 -", Model(window).History);
            Assert.DoesNotContain("live-only-secret", Model(window).History);
            Assert.Contains("Completed", Model(window).History);
            window.Close();
            await UntilAsync(() => session.Disposed);
            Assert.Contains(source, session.Released);
        }
        finally { window.Close(); await UntilAsync(() => session.Disposed); }
    }

    [AvaloniaFact]
    public async Task BlockingAndFailingRuntimeGetterLeavesDispatcherResponsiveAndDisplaysCachedError()
    {
        using var getter = new GetterProbe();
        await using var runtime = new PowerShellSession();
        var session = new LifecycleSession
        {
            Query = request => runtime.InvokeForTestingAsync("param($fixture) $fixture", request.Token, [getter]),
            Release = runtime.ReleaseResult
        };
        var window = new MainWindow(session);
        window.Show();
        try
        {
            await getter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(getter.RanOnDispatcher);
            Assert.True(Model(window).IsBusy);
            Assert.Contains("pending", Model(window).Status);
            Assert.Empty(Grid(window).ItemsSource?.Cast<ConsoleRow>() ?? []);
            var dispatched = false;
            await Dispatcher.UIThread.InvokeAsync(() => dispatched = true, DispatcherPriority.Background);
            Assert.True(dispatched);
            getter.Release.Set();
            await UntilAsync(() => !Model(window).IsBusy);
            window.UpdateLayout();
            var row = Assert.Single(Grid(window).ItemsSource!.Cast<ConsoleRow>());
            Assert.Equal("(unavailable)", row.Cells["Value"].Display);
            Assert.Contains("gated-getter-error", row.Cells["Value"].Error);
            Assert.Contains("CompletedWithErrors", Model(window).Status);
            Assert.Contains(Grid(window).GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text == "(unavailable)" && ToolTip.GetTip(text)?.ToString()?.Contains("gated-getter-error") == true);
            var evaluations = getter.Evaluations;
            Grid(window).SelectedItem = row;
            window.FindControl<TextBox>("FilterBox")!.Text = "unavailable";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Equal(evaluations, getter.Evaluations);
        }
        finally
        {
            getter.Release.Set();
            window.Close();
            await UntilAsync(() => session.Disposed);
        }
    }

    public sealed class GetterProbe : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool RanOnDispatcher { get; private set; }
        public int Evaluations { get; private set; }
        public string Value
        {
            get
            {
                RanOnDispatcher |= Dispatcher.UIThread.CheckAccess();
                Evaluations++;
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Getter fixture was not released.");
                throw new InvalidOperationException("gated-getter-error");
            }
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class QueryRequest(ConsoleNode node, CancellationToken token,
        Func<HostPrompt, CancellationToken, Task<HostResponse?>>? prompt)
    {
        public ConsoleNode Node { get; } = node;
        public CancellationToken Token { get; } = token;
        public Func<HostPrompt, CancellationToken, Task<HostResponse?>>? Prompt { get; } = prompt;
    }

    private sealed class LifecycleSession : IConsoleSession, IInvocationHostSession
    {
        public string RuntimeVersion => "fixture";
        public Func<HostPrompt, CancellationToken, Task<HostResponse?>>? PromptHandler { get; set; }
        public event Action<InvocationStatus>? StateChanged;
        public bool StopInvocation(Guid invocationId) => false;
        public Func<QueryRequest, Task<ConsoleResult>> Query { get; set; } = _ => Task.FromResult(Result("initial"));
        public Func<Task<ConsoleResult>> Execute { get; set; } =
            () => throw new NotSupportedException("This fixture action has not been configured.");
        public Func<CancellationToken, Task<IReadOnlyList<ObjectProperty>>> Inspect { get; set; } =
            _ => Task.FromResult<IReadOnlyList<ObjectProperty>>([new("Name", "string", "fixture")]);
        public CancellationToken InspectionToken { get; private set; }
        public Action<Guid>? Release { get; init; }
        public List<QueryRequest> Queries { get; } = [];
        public int FinishedQueries { get; private set; }
        public HashSet<Guid> LiveResults { get; } = [];
        public List<Guid> Released { get; } = [];
        public bool Disposed { get; private set; }

        public async Task<ConsoleResult> QueryAsync(ConsoleNode node, CancellationToken cancellationToken = default)
        {
            var request = new QueryRequest(node, cancellationToken, PromptHandler);
            Queries.Add(request);
            try
            {
                var result = await Query(request);
                LiveResults.Add(result.Id);
                return result;
            }
            finally { FinishedQueries++; }
        }

        public async Task<ConsoleResult> ExecuteAsync(ConsoleActionId action, Guid resultId, IReadOnlyList<Guid> selection,
            IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            var result = await Execute();
            LiveResults.Add(result.Id);
            return result;
        }

        public Task<IReadOnlyList<ObjectProperty>> InspectAsync(Guid resultId, Guid handle, CancellationToken cancellationToken = default)
        {
            if (!LiveResults.Contains(resultId)) throw new InvalidOperationException("Result released.");
            InspectionToken = cancellationToken;
            return Inspect(cancellationToken);
        }

        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderInfo>>([]);
        public Task<IReadOnlyList<ConsoleNode>> GetDriveNodesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConsoleNode>>([]);
        public void ReleaseResult(Guid resultId)
        {
            LiveResults.Remove(resultId);
            Released.Add(resultId);
            Release?.Invoke(resultId);
        }

        public void Notify(Guid invocationId, InvocationState state) =>
            StateChanged?.Invoke(new(Guid.Empty, invocationId, state));

        public ValueTask DisposeAsync()
        {
            LiveResults.Clear();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
