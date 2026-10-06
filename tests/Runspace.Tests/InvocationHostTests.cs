using System.Diagnostics;
using System.Security;
using Runspace.Core;
using Runspace.PowerShell;
using Xunit.Abstractions;

namespace Runspace.Tests;

public sealed class InvocationHostTests(ITestOutputHelper output)
{
    private static HostResponse Response(string name, object? value) => new(new Dictionary<string, object?> { [name] = value });
    private static TaskCompletionSource<HostPrompt> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly ConsoleNode Providers = BuiltInCatalog.LocalSystem.Single(node => node.Kind == ResourceKind.Providers);
    private static void Completed(ConsoleResult result) => Assert.True(result.Outcome == InvocationOutcome.Completed,
        $"{result.Outcome}: {string.Join("\n", result.Diagnostics.Select(record => record.Message))}");
    private static async Task AwaitPrompt(Task signal, Task<ConsoleResult> running)
    {
        var completed = await Task.WhenAny(signal, running).WaitAsync(TimeSpan.FromSeconds(10));
        if (completed != signal)
            Assert.Fail(string.Join("\n", (await running).Diagnostics.Select(record => record.Message)));
    }

    [Fact]
    public async Task ValidatesRequiredTypedParametersAndPreservesInvocationIdentity()
    {
        await using var session = new PowerShellSession();
        var prompts = new List<HostPrompt>();
        session.PromptHandler = async (prompt, token) =>
        {
            prompts.Add(prompt);
            Assert.Equal(HostPromptKind.Fields, prompt.Kind);
            Assert.Equal("System.Int32", Assert.Single(prompt.Fields).TypeName);
            using var empty = Response("Count", "");
            using var invalid = Response("Count", "not-an-integer");
            Assert.NotNull(await prompt.ValidateAsync(empty));
            Assert.NotNull(await prompt.ValidateAsync(invalid));
            var valid = Response("Count", "42");
            Assert.Null(await prompt.ValidateAsync(valid));
            return valid;
        };
        var result = await session.InvokeForTestingAsync("function Test-Required { param([Parameter(Mandatory)][int]$Count) $Count }; Test-Required");
        Completed(result);
        Assert.Equal(42, Assert.Single(result.Rows).Cells["Value"].Value);
        Assert.Equal(result.Id, Assert.Single(prompts).InvocationId);
        Assert.Contains("Non-replayable", result.Script);
        var second = await session.InvokeForTestingAsync("Test-Required");
        Assert.Equal(InvocationOutcome.Completed, second.Outcome);
        Assert.Equal(prompts[0].SessionId, prompts[1].SessionId);
        Assert.NotEqual(prompts[0].InvocationId, prompts[1].InvocationId);
        Assert.NotEqual(prompts[0].Id, prompts[1].Id);
    }

    [Fact]
    public async Task AnswersChoiceAndHonorsDefaultWithoutAcceptingInvalidIndexes()
    {
        await using var session = new PowerShellSession();
        session.PromptHandler = async (prompt, token) =>
        {
            Assert.Equal(HostPromptKind.Choice, prompt.Kind);
            Assert.Equal(1, prompt.DefaultChoice);
            Assert.Equal(["&First", "&Second"], prompt.Choices.Select(choice => choice.Label));
            using var invalid = new HostResponse(new Dictionary<string, object?>(), 9);
            Assert.NotNull(await prompt.ValidateAsync(invalid));
            return new HostResponse(new Dictionary<string, object?>(), 0);
        };
        var result = await session.InvokeForTestingAsync("""
            $choices = [Management.Automation.Host.ChoiceDescription[]]@(
                [Management.Automation.Host.ChoiceDescription]::new('&First', 'First help'),
                [Management.Automation.Host.ChoiceDescription]::new('&Second', 'Second help'))
            $Host.UI.PromptForChoice('Choose', 'Fixed invocation', $choices, 1)
            """);
        Assert.Equal(InvocationOutcome.Completed, result.Outcome);
        Assert.Equal(0, Assert.Single(result.Rows).Cells["Value"].Value);
    }

    [Fact]
    public async Task TypedBooleanFieldBindsFalseRatherThanTruthyNonemptyString()
    {
        await using var session = new PowerShellSession();
        session.PromptHandler = async (prompt, token) =>
        {
            using var invalid = Response("Enabled", "not-a-boolean");
            Assert.NotNull(await prompt.ValidateAsync(invalid));
            return Response("Enabled", "False");
        };
        var result = await session.InvokeForTestingAsync("function Test-Boolean { param([Parameter(Mandatory)][bool]$Enabled) $Enabled }; Test-Boolean");
        Completed(result);
        Assert.Equal(false, Assert.Single(result.Rows).Cells["Value"].Value);
    }

    [Fact]
    public async Task NullPipelineValuesDoNotCreateHandlesButNullObjectPropertiesRemainVisible()
    {
        await using var session = new PowerShellSession();
        var result = await session.InvokeForTestingAsync("$null; [pscustomobject]@{ Value = $null }");
        Completed(result);
        Assert.Null(Assert.Single(result.Rows).Cells["Value"].Value);
        Assert.Equal("(null)", result.Rows[0].Cells["Value"].Display);
        session.ReleaseResult(result.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsCredentialsAndSecureInputWithoutRetainingEchoedSecrets(bool credential)
    {
        await using var session = new PowerShellSession();
        SecureString? password = null;
        session.PromptHandler = (prompt, token) =>
        {
            password = new SecureString();
            foreach (var character in "synthetic-host-secret") password.AppendChar(character);
            password.MakeReadOnly();
            Assert.Equal(credential ? HostPromptKind.Credential : HostPromptKind.SecureInput, prompt.Kind);
            return Task.FromResult<HostResponse?>(credential
                ? Response("Credential", new HostCredential("synthetic-user", password))
                : Response(Assert.Single(prompt.Fields).Name, password));
        };
        var script = credential
            ? """
                $c = $Host.UI.PromptForCredential('Credentials', 'Fixture only', '', '')
                if ($c.UserName -ne 'synthetic-user' -or $c.GetNetworkCredential().Password -ne 'synthetic-host-secret') { throw 'binding failed' }
                $text = $c.GetNetworkCredential().Password
                """
            : """
                $s = Read-Host 'Secret' -AsSecureString
                $text = [Net.NetworkCredential]::new('', $s).Password
                if ($text -ne 'synthetic-host-secret') { throw 'binding failed' }
                """;
        var result = await session.InvokeForTestingAsync(script + """

            Write-Warning $text
            Write-Information $text -InformationAction Continue
            $text
            throw $text
            """);
        Assert.Equal(InvocationOutcome.Failed, result.Outcome);
        Assert.Empty(result.Rows);
        Assert.True(result.OutputSuppressed);
        Assert.DoesNotContain("synthetic-host-secret", result.Script);
        Assert.DoesNotContain("synthetic-user", result.Script);
        Assert.All(result.Diagnostics, record => Assert.DoesNotContain("synthetic-host-secret", record.Message));
        Assert.Contains("redacted", result.Script);
        Assert.NotNull(password);
        Assert.Throws<ObjectDisposedException>(() => password.Length);
        Assert.Equal(InvocationOutcome.Completed, (await session.QueryAsync(Providers)).Outcome);
    }

    [Fact]
    public async Task SuccessfulSecureInputExplicitlyReportsSuppressedOutput()
    {
        await using var session = new PowerShellSession();
        session.PromptHandler = (prompt, token) =>
        {
            var secure = new SecureString();
            secure.AppendChar('x');
            return Task.FromResult<HostResponse?>(Response("Value", secure));
        };
        var result = await session.InvokeForTestingAsync("$null = $Host.UI.ReadLineAsSecureString(); 'useful but withheld'");
        Completed(result);
        Assert.True(result.OutputSuppressed);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Diagnostics, record => record.Message.Contains("output is not retained"));
    }

    [Fact]
    public async Task CancelledPromptReleasesCallbackAndSerializedSession()
    {
        await using var session = new PowerShellSession();
        var signal = Signal();
        using var cancellation = new CancellationTokenSource();
        session.PromptHandler = async (prompt, token) =>
        {
            signal.SetResult(prompt);
            await Task.Delay(Timeout.Infinite, token);
            return null;
        };
        var running = session.InvokeForTestingAsync("'partial'; Read-Host 'Wait'; 'never'", cancellation.Token);
        await AwaitPrompt(signal.Task, running);
        var queued = session.QueryAsync(Providers);
        var watch = Stopwatch.StartNew();
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(InvocationOutcome.Cancelled, result.Outcome);
        Assert.Equal("partial", Assert.Single(result.Rows).Cells["Value"].Value);
        Assert.Equal(InvocationOutcome.Completed, (await queued.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        output.WriteLine($"Prompt stop: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task PromptDismissalIsCancelledAndSessionDisposalCancelsWaitingInput()
    {
        await using var session = new PowerShellSession();
        session.PromptHandler = (prompt, token) => Task.FromResult<HostResponse?>(null);
        var dismissed = await session.InvokeForTestingAsync("'partial'; Read-Host 'Cancel'; 'never'");
        Assert.Equal(InvocationOutcome.Cancelled, dismissed.Outcome);
        Assert.Equal("partial", Assert.Single(dismissed.Rows).Cells["Value"].Value);
        var signal = Signal();
        session.PromptHandler = async (prompt, token) =>
        {
            signal.SetResult(prompt);
            await Task.Delay(Timeout.Infinite, token);
            return null;
        };
        var waiting = session.InvokeForTestingAsync("$Host.UI.ReadLine()");
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(InvocationOutcome.Cancelled, (await waiting).Outcome);
    }

    [Fact]
    public async Task BoundStopRejectsOldInvocationWithoutCancellingNewPrompt()
    {
        await using var session = new PowerShellSession();
        var signal = Signal();
        session.PromptHandler = async (prompt, token) =>
        {
            signal.SetResult(prompt);
            await Task.Delay(Timeout.Infinite, token);
            return null;
        };
        var first = session.InvokeForTestingAsync("$Host.UI.ReadLine()");
        var oldPrompt = await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.StopInvocation(oldPrompt.InvocationId));
        Assert.Equal(InvocationOutcome.Cancelled, (await first.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
        signal = Signal();
        var second = session.InvokeForTestingAsync("$Host.UI.ReadLine()");
        var currentPrompt = await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.StopInvocation(oldPrompt.InvocationId));
        Assert.False(second.IsCompleted);
        Assert.True(session.StopInvocation(currentPrompt.InvocationId));
        Assert.Equal(InvocationOutcome.Cancelled, (await second.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
    }

    [Fact]
    public async Task StopSleepReachesCancelledWithinTwoSecondsAndNextQuerySucceeds()
    {
        await using var session = new PowerShellSession();
        var signal = Signal();
        session.PromptHandler = (prompt, token) =>
        {
            signal.SetResult(prompt);
            return Task.FromResult<HostResponse?>(Response("Value", "ready"));
        };
        using var cancellation = new CancellationTokenSource();
        var running = session.InvokeForTestingAsync("'partial'; $null = $Host.UI.ReadLine(); Start-Sleep -Seconds 30; 'never'", cancellation.Token);
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(150);
        var watch = Stopwatch.StartNew();
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(InvocationOutcome.Cancelled, result.Outcome);
        Assert.Equal("partial", Assert.Single(result.Rows).Cells["Value"].Value);
        Assert.Equal(InvocationOutcome.Completed, (await session.QueryAsync(Providers)).Outcome);
        output.WriteLine($"Start-Sleep stop-to-terminal: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public async Task NativeCallRemainsUnresponsiveUntilItReturnsWithoutDisposingPipeline(int promptDelayMilliseconds)
    {
        await using var session = new PowerShellSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEvent(false);
        var unresponsive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += status => { if (status.State == InvocationState.Unresponsive) unresponsive.TrySetResult(); };
        session.PromptHandler = async (prompt, token) =>
        {
            await Task.Delay(promptDelayMilliseconds, token);
            return Response("Value", "ready");
        };
        using var cancellation = new CancellationTokenSource();
        var running = session.InvokeForTestingAsync("""
            param($fixture)
            $null = $Host.UI.ReadLine()
            $fixture.Wait()
            'never'
            """, cancellation.Token, [new NativeCallFixture(entered, release)]);
        try
        {
            await AwaitPrompt(entered.Task, running);
            cancellation.Cancel();
            var query = session.QueryAsync(Providers);
            await unresponsive.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted);
            Assert.False(query.IsCompleted);
            release.Set();
            Assert.Equal(InvocationOutcome.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
            Assert.Equal(InvocationOutcome.Completed, (await query.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        }
        finally
        {
            release.Set();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public sealed class NativeCallFixture(TaskCompletionSource entered, WaitHandle release)
    {
        public void Wait()
        {
            // Signal from inside the non-cooperative call, with no intervening PowerShell cancellation checkpoint.
            entered.TrySetResult();
            if (!release.WaitOne(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The native-call test fixture was not released.");
        }
    }

    [Fact]
    public async Task UnsupportedTerminalAndMissingPromptHandlerFailExplicitly()
    {
        await using var session = new PowerShellSession();
        var missing = await session.InvokeForTestingAsync("$ErrorActionPreference = 'Stop'; Read-Host 'Unavailable'");
        Assert.Equal(InvocationOutcome.Failed, missing.Outcome);
        Assert.Contains(missing.Diagnostics, record => record.Message.Contains("no prompt handler"));
        var raw = await session.InvokeForTestingAsync("$ErrorActionPreference = 'Stop'; $Host.UI.RawUI.ReadKey()");
        Assert.Equal(InvocationOutcome.Failed, raw.Outcome);
        Assert.Contains(raw.Diagnostics, record => record.Message.Contains("Raw terminal"));
    }

    [Fact]
    public async Task PreviewAndConfirmedMutationUseOriginalDisposableChildDespiteCallerSelectionChanges()
    {
        await using var session = new PowerShellSession();
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), "-n 30 127.0.0.1")
            : new ProcessStartInfo("sleep", "30");
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        using var child = Process.Start(start)!;
        try
        {
            var processes = await session.QueryAsync(BuiltInCatalog.LocalSystem.Single(node => node.Kind == ResourceKind.Processes));
            var original = Assert.Single(processes.Rows, row => Equals(row.Cells["Id"].Value, child.Id));
            var current = Assert.Single(processes.Rows, row => Equals(row.Cells["Id"].Value, Environment.ProcessId));
            Assert.Contains(await session.InspectAsync(processes.Id, original.Handle), property =>
                property.Name == "Id" && property.Value == child.Id.ToString());
            var preview = await session.ExecuteAsync(ConsoleActionId.StopProcess, processes.Id, [original.Handle],
                new Dictionary<string, string> { ["WhatIf"] = "True", ["Confirm"] = "True" });
            Completed(preview);
            Assert.False(child.HasExited);
            Assert.Contains("-WhatIf:$true", preview.Script);
            Assert.Contains(preview.Diagnostics, record => record.Message.Contains("What if", StringComparison.OrdinalIgnoreCase));
            var awaiting = Signal();
            var answer = new TaskCompletionSource<HostResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.PromptHandler = (prompt, token) => { awaiting.SetResult(prompt); return answer.Task; };
            Guid[] selection = [original.Handle];
            var confirmed = session.ExecuteAsync(ConsoleActionId.StopProcess, processes.Id, selection,
                new Dictionary<string, string> { ["Confirm"] = "True" });
            var prompt = await awaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            selection[0] = current.Handle;
            var yes = prompt.Choices.Select((choice, index) => (choice, index))
                .Single(item => item.choice.Label.Replace("&", string.Empty) == "Yes").index;
            answer.SetResult(new HostResponse(new Dictionary<string, object?>(), yes));
            var result = await confirmed.WaitAsync(TimeSpan.FromSeconds(10));
            Completed(result);
            Assert.Equal(result.Id, prompt.InvocationId);
            Assert.True(child.WaitForExit(5_000));
            Assert.Contains(child.Id.ToString(), result.Script);
            Assert.Contains("Non-replayable", result.Script);
            using var replacement = Process.Start(start)!;
            try
            {
                var prompts = 0;
                session.PromptHandler = (_, _) =>
                {
                    prompts++;
                    return Task.FromResult<HostResponse?>(null);
                };
                foreach (var action in new[]
                {
                    ConsoleActionId.StopProcess, ConsoleActionId.ProcessModules, ConsoleActionId.ProcessThreads,
                    ConsoleActionId.SetProcessPriority
                })
                {
                    if (action == ConsoleActionId.SetProcessPriority && !OperatingSystem.IsWindows()) continue;
                    var stale = await session.ExecuteAsync(action, processes.Id, [original.Handle],
                        new Dictionary<string, string> { ["Confirm"] = "True", ["Priority"] = "Normal" });
                    Assert.Equal(InvocationOutcome.Failed, stale.Outcome);
                    Assert.Empty(stale.Rows);
                    Assert.Contains(stale.Diagnostics, record => record.Stream == "Error" && record.Message.Contains("exited"));
                    Assert.Contains(child.Id.ToString(), stale.Script);
                    Assert.False(replacement.HasExited);
                }
                var refreshed = await session.QueryAsync(BuiltInCatalog.LocalSystem.Single(node => node.Kind == ResourceKind.Processes));
                Assert.Contains(refreshed.Rows, row => Equals(row.Cells["Id"].Value, replacement.Id));
                Assert.DoesNotContain(refreshed.Rows, row => row.Handle == original.Handle);
                var mismatched = await session.ExecuteAsync(ConsoleActionId.StopProcess, refreshed.Id, [original.Handle],
                    new Dictionary<string, string> { ["Confirm"] = "True" });
                Assert.Equal(InvocationOutcome.Failed, mismatched.Outcome);
                Assert.Contains(mismatched.Diagnostics, record => record.Message.Contains("stale"));
                Assert.Equal(0, prompts);
                Assert.False(replacement.HasExited);
            }
            finally
            {
                if (!replacement.HasExited) { replacement.Kill(entireProcessTree: true); replacement.WaitForExit(); }
            }
            Completed(await session.QueryAsync(Providers));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); }
        }
    }
}
