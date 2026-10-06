using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Runspace.Core;
using AutomationShell = System.Management.Automation.PowerShell;
using ProviderInfo = Runspace.Core.ProviderInfo;

namespace Runspace.PowerShell;

/// <summary>A local, profile-free PowerShell runspace with serialized built-in operations.</summary>
public sealed class PowerShellSession : IConsoleSession, IInvocationHostSession
{
    private readonly SemaphoreSlim queue = new(1, 1);
    private readonly object resultLock = new();
    private readonly Dictionary<Guid, StoredResult> results = [];
    private System.Management.Automation.Runspaces.Runspace? runspace;
    private bool disposed;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly InvocationHost host;
    private DiagnosticBuffer? activeDiagnostics;
    private InvocationCapture? activeCapture;

    public PowerShellSession() => host = new((stream, message) =>
    {
        activeDiagnostics?.Add(Diagnostic(stream, message));
        if (activeDiagnostics?.ErrorCount >= RetentionPolicy.ErrorRecords)
            activeCapture?.StopForLimit($"the {RetentionPolicy.ErrorRecords:N0}-error budget was reached");
    });
    public Func<HostPrompt, CancellationToken, Task<HostResponse?>>? PromptHandler { get; set; }
    public event Action<InvocationStatus>? StateChanged;
    public bool StopInvocation(Guid invocationId) => host.Current?.Stop(invocationId) ?? false;

    public string RuntimeVersion => PSVersionInfo.PSVersion.ToString();

    public Task<ConsoleResult> QueryAsync(ConsoleNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        var resolvedNode = node;
        var query = node.Kind == ResourceKind.ProviderPath ? null : QueryDefinition.For(node);
        var description = query?.Description ?? PowerShellDisplay.Command("Get-ChildItem", ("LiteralPath", node.Path));
        return RunAsync(query?.Columns ?? [], description, node.Kind, cancellationToken, diagnostics =>
        {
            if (((node.Kind != ResourceKind.ProviderPath && node.WindowsOnly) || RequiresWindows(node.Kind)) && !OperatingSystem.IsWindows())
            {
                diagnostics.Add(Diagnostic("Error", $"{node.Name} is available only on Windows."));
                return new Invocation([], InvocationOutcome.Failed);
            }
            if (node.Kind == ResourceKind.ProviderPath)
            {
                if (string.IsNullOrEmpty(node.Path)) throw new ArgumentException("A provider path is required.");
                runspace!.SessionStateProxy.Path.GetUnresolvedProviderPathFromPSPath(node.Path, out var provider, out var drive);
                resolvedNode = node with
                {
                    ProviderName = provider.Name,
                    WindowsOnly = provider.Name is "Registry" or "Certificate"
                };
                query = QueryDefinition.For(resolvedNode);
                diagnostics.Add(Diagnostic("Information", $"Source provider: {provider.Name}; drive: {drive?.Name ?? "(provider-qualified path)"}."));
            }

            if (node.Kind == ResourceKind.NetworkProperties)
                return new Invocation([PSObject.AsPSObject(IPGlobalProperties.GetIPGlobalProperties())], InvocationOutcome.Completed);
            if (node.Kind == ResourceKind.NetworkInterfaces)
                return new Invocation(NetworkInterface.GetAllNetworkInterfaces().Select(PSObject.AsPSObject).ToArray(), InvocationOutcome.Completed);
            if (node.Kind == ResourceKind.ManagedComputers)
                return new Invocation([PSObject.AsPSObject(new
                {
                    Name = Environment.MachineName,
                    OS = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.OSArchitecture.ToString()
                })], InvocationOutcome.Completed);
            if (node.Kind == ResourceKind.Registry)
            {
                var roots = new[]
                {
                    new { Name = "HKEY_LOCAL_MACHINE", Path = "HKLM:\\" },
                    new { Name = "HKEY_CURRENT_USER", Path = "HKCU:\\" }
                };
                return new Invocation(roots.Select(PSObject.AsPSObject).ToArray(), InvocationOutcome.Completed);
            }

            using var shell = NewShell();
            query!.Configure(shell);
            return Invoke(shell, cancellationToken, diagnostics);
        }, node, finalDescription: () => query?.Description ?? description, finalColumns: () => query?.Columns ?? [],
            finalNode: () => resolvedNode);
    }

    public Task<ConsoleResult> ExecuteAsync(ConsoleActionId action, Guid resultId, IReadOnlyList<Guid> selection,
        IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(parameters);
        selection = selection.ToArray();
        parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        var resultKind = action switch
        {
            ConsoleActionId.ProcessModules => ResourceKind.ProcessModules,
            ConsoleActionId.ProcessThreads => ResourceKind.ProcessThreads,
            ConsoleActionId.StartProcess => ResourceKind.Processes,
            _ => ResourceKind.Overview
        };
        var columns = resultKind == ResourceKind.Overview ? [] : QueryDefinition.ProcessColumns(resultKind);
        var description = ActionDescription(action, parameters, [], selection);
        return RunAsync(columns, description, resultKind, cancellationToken, diagnostics =>
        {
            using var lease = AcquireResult(resultId);
            var stored = lease.Result;
            LiveRow[] selected;
            lock (resultLock)
            {
                ValidateAction(action, stored.Kind, selection.Count);
                selected = selection.Distinct().Select(handle => stored.Rows.TryGetValue(handle, out var row)
                    ? row : throw new InvalidOperationException("A selected object is stale. Refresh the view before making changes.")).ToArray();
            }
            description = ActionDescription(action, parameters, selected, selection);
            if (action == ConsoleActionId.StartProcess)
            {
                using var shell = NewShell();
                shell.AddCommand("Start-Process").AddParameter("FilePath", Required(parameters, "FilePath"))
                    .AddParameter("PassThru").AddParameter("ErrorAction", ActionPreference.Stop);
                if (parameters.TryGetValue("Arguments", out var arguments) && !string.IsNullOrWhiteSpace(arguments))
                    shell.AddParameter("ArgumentList", arguments);
                ConfigureShouldProcess(shell, parameters);
                return Invoke(shell, cancellationToken, diagnostics);
            }
            if (action == ConsoleActionId.AddDrive)
            {
                using var shell = NewShell();
                shell.AddCommand("New-PSDrive")
                    .AddParameter("Name", Required(parameters, "Name"))
                    .AddParameter("PSProvider", Required(parameters, "Provider"))
                    .AddParameter("Root", Required(parameters, "Root"))
                    .AddParameter("Scope", "Global")
                    .AddParameter("ErrorAction", ActionPreference.Stop);
                ConfigureShouldProcess(shell, parameters);
                return Invoke(shell, cancellationToken, diagnostics);
            }

            var output = new List<PSObject>();
            var errors = false;
            var successes = 0;
            foreach (var row in selected)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new Invocation(output, InvocationOutcome.Cancelled);
                // ReleaseResult can race with an operation queued by the UI.
                lock (resultLock)
                {
                    if (!results.ContainsKey(resultId))
                        throw new InvalidOperationException("The selected result was released. Refresh before making changes.");
                }
                try
                {
                    using var shell = NewShell();
                    ConfigureAction(shell, action, row, parameters);
                    ConfigureShouldProcess(shell, parameters);
                    var invocation = Invoke(shell, cancellationToken, diagnostics);
                    output.AddRange(invocation.Output);
                    if (invocation.Outcome == InvocationOutcome.Cancelled)
                        return new Invocation(output, InvocationOutcome.Cancelled);
                    if (activeCapture?.LimitReached == true)
                        return new Invocation(output, InvocationOutcome.Failed);
                    errors |= invocation.Outcome != InvocationOutcome.Completed;
                    if (invocation.Outcome is InvocationOutcome.Completed or InvocationOutcome.CompletedWithErrors)
                        successes++;
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
                {
                    diagnostics.Add(Diagnostic("Error", exception.Message));
                    errors = true;
                }
            }
            return new Invocation(output, errors
                ? successes > 0 ? InvocationOutcome.CompletedWithErrors : InvocationOutcome.Failed
                : InvocationOutcome.Completed);
        }, finalDescription: () => description);
    }

    public async Task<IReadOnlyList<ObjectProperty>> InspectAsync(Guid resultId, Guid handle,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellationToken = linked.Token;
        await queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run<IReadOnlyList<ObjectProperty>>(() =>
            {
                ThrowIfDisposed();
                using var context = new DefaultRunspaceScope(runspace);
                using var lease = AcquireResult(resultId);
                PSObject value;
                lock (resultLock)
                    value = lease.Result.Rows.TryGetValue(handle, out var row)
                        ? row.Value
                        : throw new InvalidOperationException("This object is stale or its result has been released.");
                var properties = new List<ObjectProperty>();
                try
                {
                    foreach (var property in value.Properties)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var propertyValue = property.Value;
                            properties.Add(new ObjectProperty(property.Name, property.TypeNameOfValue, Display(propertyValue)));
                        }
                        catch (Exception exception)
                        {
                            properties.Add(new ObjectProperty(property.Name, "(unavailable)", "(unavailable)", PropertyError(exception)));
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    properties.Add(new ObjectProperty("(Property enumeration)", value.BaseObject.GetType().FullName ?? "Object",
                        "(unavailable)", PropertyError(exception)));
                }
                if (OperatingSystem.IsWindows() && value.BaseObject is Microsoft.Win32.RegistryKey registryKey)
                {
                    try
                    {
                        foreach (var name in registryKey.GetValueNames())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                properties.Add(new ObjectProperty(string.IsNullOrEmpty(name) ? "(Default)" : name,
                                    registryKey.GetValueKind(name).ToString(), Display(registryKey.GetValue(name))));
                            }
                            catch (Exception exception)
                            {
                                properties.Add(new ObjectProperty(name, "Registry value", "(unavailable)", PropertyError(exception)));
                            }
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        properties.Add(new ObjectProperty("Registry values", "Registry", "(unavailable)", PropertyError(exception)));
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                return properties;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }

    public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(new ConsoleNode("providers", "Providers", ResourceKind.Providers, "Session providers"), cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSuccessful(result);
            return result.Rows.Select(row => new ProviderInfo(row.Cells["Name"].Display, row.Cells["Capabilities"].Display)).ToArray();
        }
        finally { ReleaseResult(result.Id); }
    }

    public async Task<IReadOnlyList<ConsoleNode>> GetDriveNodesAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(new ConsoleNode("drives", "Drives", ResourceKind.Drives, "Session drives"), cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSuccessful(result);
            return result.Rows.Select(row => row.RelatedNode!).Where(node => node is not null).ToArray();
        }
        finally { ReleaseResult(result.Id); }
    }

    public void ReleaseResult(Guid resultId)
    {
        lock (resultLock)
        {
            if (!results.Remove(resultId, out var result)) return;
            result.Released = true;
            if (result.Readers == 0) result.DisposeObjects();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await queue.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            lock (resultLock)
            {
                foreach (var result in results.Values) result.DisposeObjects();
                results.Clear();
            }
            await Task.Run(() => runspace?.Dispose()).ConfigureAwait(false);
            runspace = null;
        }
        finally { queue.Release(); }
    }

    // Internal scripts are limited to runtime validation and the test assembly.
    internal Task<ConsoleResult> InvokeForTestingAsync(string script, CancellationToken cancellationToken = default,
        IReadOnlyList<object>? arguments = null, IReadOnlyList<ConsoleColumn>? columns = null) =>
        RunAsync(columns ?? [new("Value", "Value")], "Internal runtime test", ResourceKind.Overview, cancellationToken, diagnostics =>
        {
            using var shell = NewShell();
            shell.AddScript(script);
            foreach (var argument in arguments ?? []) shell.AddArgument(argument);
            return Invoke(shell, cancellationToken, diagnostics);
        });

    private async Task<ConsoleResult> RunAsync(IReadOnlyList<ConsoleColumn> columns, string description, ResourceKind kind,
        CancellationToken cancellationToken, Func<DiagnosticBuffer, Invocation> operation, ConsoleNode? node = null,
        Func<string>? finalDescription = null, Func<IReadOnlyList<ConsoleColumn>>? finalColumns = null,
        Func<ConsoleNode>? finalNode = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new DiagnosticBuffer();
        var capture = new InvocationCapture(stopwatch);
        var id = Guid.NewGuid();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        using var invocationContext = new InvocationContext(sessionId, id, linked.Token, PromptHandler,
            status => StateChanged?.Invoke(status));
        cancellationToken = invocationContext.Token;
        try
        {
            await queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ConsoleResult(id, columns, [], finalDescription?.Invoke() ?? description, [], stopwatch.Elapsed, InvocationOutcome.Cancelled);
        }
        try
        {
            return await Task.Run(() =>
            {
                ThrowIfDisposed();
                host.Current = invocationContext;
                activeDiagnostics = diagnostics;
                activeCapture = capture;
                using var stopping = cancellationToken.Register(() =>
                {
                    invocationContext.Notify(InvocationState.Stopping);
                    _ = ReportUnresponsiveAsync(invocationContext);
                });
                invocationContext.Notify(InvocationState.Running);
                using var context = new DefaultRunspaceScope(null);
                Invocation invocation;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureRunspace();
                    System.Management.Automation.Runspaces.Runspace.DefaultRunspace = runspace;
                    invocation = operation(diagnostics);
                }
                catch (OperationCanceledException)
                {
                    invocation = new Invocation([], InvocationOutcome.Cancelled);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(Diagnostic("Error", exception.ToString()));
                    invocation = new Invocation([], cancellationToken.IsCancellationRequested ? InvocationOutcome.Cancelled : InvocationOutcome.Failed);
                }
                if (cancellationToken.IsCancellationRequested)
                    invocation = invocation with { Outcome = InvocationOutcome.Cancelled };
                var resolvedColumns = finalColumns?.Invoke() ?? columns;
                var resolvedNode = finalNode?.Invoke() ?? node;
                var stored = new StoredResult(kind);
                var failures = new DisplayFailures(diagnostics);
                if (invocation.Output.Count > RetentionPolicy.ResultRows)
                {
                    var kept = invocation.Output.Take(RetentionPolicy.ResultRows).Select(value => value.BaseObject)
                        .ToHashSet(ReferenceEqualityComparer.Instance);
                    DisposeOutput(invocation.Output.Skip(RetentionPolicy.ResultRows).Where(value => !kept.Contains(value.BaseObject)));
                    capture.StopForLimit($"more than {RetentionPolicy.ResultRows:N0} output objects were produced");
                }
                // Secure host input can be echoed or embedded in arbitrary result objects.
                // Do not retain those object graphs or textual streams in the console.
                var rows = invocationContext.HasSecrets ? [] :
                    invocation.Output.Take(RetentionPolicy.ResultRows)
                        .Select((value, index) => Materialize(value, resolvedColumns, stored, resolvedNode, index + 1, failures)).ToArray();
                if (invocationContext.HasSecrets)
                    DisposeOutput(invocation.Output);
                failures.Summarize();
                var outcome = invocation.Outcome == InvocationOutcome.Completed && failures.Count > 0
                    ? InvocationOutcome.CompletedWithErrors : invocation.Outcome;
                if (cancellationToken.IsCancellationRequested) outcome = InvocationOutcome.Cancelled;
                var notices = new List<string>();
                if (capture.LimitReached)
                {
                    notices.Add($"Execution stopped by retention policy: {capture.LimitReason}. Retained results are incomplete. " +
                        "Export the retained visible rows before leaving; refresh/requery a narrower scope, " +
                        "or deliberately run a replayable read-only command in a separate PowerShell with Export-Csv/file logging. " +
                        "Do not automatically replay mutations, interactive or redacted commands.");
                    if (outcome != InvocationOutcome.Cancelled) outcome = InvocationOutcome.Failed;
                }
                lock (resultLock)
                {
                    if (stored.Rows.Count > 0 && results.Count >= RetentionPolicy.ResultSets)
                    {
                        stored.DisposeObjects();
                        rows = [];
                        var executionOutcome = outcome;
                        if (outcome != InvocationOutcome.Cancelled) outcome = InvocationOutcome.Failed;
                        notices.Add($"The {RetentionPolicy.ResultSets}-live-result budget is full. New output was released, not retained; existing handles remain valid. " +
                            $"Execution outcome before admission: {executionOutcome}; side effects may remain. " +
                            "Export/release an existing view and requery. No complete-output export is possible from this discarded result.");
                    }
                    if (stored.Rows.Count > 0) results.Add(id, stored);
                }
                var script = finalDescription?.Invoke() ?? description;
                if (invocationContext.HasInput)
                    script = "# Non-replayable: interactive host input is not recorded.\n" + script;
                if (invocationContext.HasSecrets)
                {
                    script = "# Non-replayable: invocation used credentials/secure input; command and input are redacted.";
                    var redacted = new DiagnosticBuffer();
                    foreach (var record in diagnostics.Snapshot())
                        redacted.Add(record with { Message = "<REDACTED: invocation used secure input>" });
                    redacted.Add(Diagnostic("Information", "Secure-input invocation output is not retained; textual diagnostics are redacted."));
                    diagnostics = redacted;
                }
                return new ConsoleResult(id, resolvedColumns, rows, script, diagnostics.Snapshot(), stopwatch.Elapsed, outcome,
                    OutputSuppressed: invocationContext.HasSecrets, RetentionNotice: notices.Count == 0 ? null : string.Join("\n", notices),
                    Measurements: new(capture.FirstOutputLatency, capture.OutputReceived, capture.PeakOutputBuffer,
                        activeDiagnostics!.ReceivedCount, activeDiagnostics.PeakCount));
            }).ConfigureAwait(false);
        }
        finally
        {
            invocationContext.Finish();
            host.Current = null;
            activeDiagnostics = null;
            activeCapture = null;
            queue.Release();
        }
    }

    private static async Task ReportUnresponsiveAsync(InvocationContext context)
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        context.Notify(InvocationState.Unresponsive);
    }

    private void EnsureRunspace()
    {
        if (runspace is not null) return;
        PowerShellPayload.Validate(AppContext.BaseDirectory, OperatingSystem.IsWindows());
        var state = InitialSessionState.CreateDefault2();
        if (OperatingSystem.IsWindows())
            state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.RemoteSigned;
        var created = RunspaceFactory.CreateRunspace(host, state);
        try
        {
            created.Open();
            runspace = created;
            // SDK deployments carry modules beside the app; no external pwsh installation or profiles are required.
            var modulePaths = new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "Modules"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", OperatingSystem.IsWindows() ? "win" : "unix", "lib", "net10.0", "Modules")
            }.Where(Directory.Exists).ToArray();
            if (modulePaths.Length > 0)
            {
                using var configure = NewShell();
                configure.AddScript("$env:PSModulePath = $args[0] + [IO.Path]::PathSeparator + $env:PSModulePath", useLocalScope: true)
                    .AddArgument(string.Join(System.IO.Path.PathSeparator, modulePaths));
                configure.Invoke();
                if (configure.HadErrors)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, configure.Streams.Error.Select(error => error.ToString())));
            }
        }
        catch
        {
            runspace = null;
            created.Dispose();
            throw;
        }
    }

    private AutomationShell NewShell()
    {
        var shell = AutomationShell.Create();
        shell.Runspace = runspace ?? throw new InvalidOperationException("The PowerShell runspace has not opened.");
        return shell;
    }

    private Invocation Invoke(AutomationShell shell, CancellationToken cancellationToken,
        DiagnosticBuffer diagnostics)
    {
        cancellationToken = host.Current?.Token ?? cancellationToken;
        using var output = new PSDataCollection<PSObject>();
        var capture = activeCapture ?? throw new InvalidOperationException("No active invocation capture.");
        var retained = new List<PSObject>();
        var unsubscribe = new List<Action>();
        var stopGate = new object();
        IAsyncResult? stop = null;
        void RequestStop()
        {
            lock (stopGate)
            {
                if (stop is not null) return;
                try { stop = shell.BeginStop(null, null); }
                catch (InvalidPowerShellStateException) { /* Completion won the stop race. */ }
            }
        }
        capture.RequestStop = RequestStop;
        void DrainOutput()
        {
            capture.PeakOutputBuffer = Math.Max(capture.PeakOutputBuffer, output.Count);
            foreach (var value in output.ReadAll())
            {
                // The SDK host can return a null sentinel when handling PipelineStoppedException.
                if (value is null) continue;
                capture.FirstOutputLatency ??= capture.Stopwatch.Elapsed;
                capture.OutputReceived++;
                if (capture.OutputReceived <= RetentionPolicy.ResultRows)
                {
                    retained.Add(value);
                    capture.RetainedObjects.Add(value.BaseObject);
                }
                else
                {
                    if (!capture.RetainedObjects.Contains(value.BaseObject) && value.BaseObject is IDisposable disposable)
                        disposable.Dispose();
                    capture.StopForLimit($"more than {RetentionPolicy.ResultRows:N0} output objects were produced");
                }
            }
        }
        void Watch<T>(PSDataCollection<T> stream, string name, Func<T, string> message)
        {
            EventHandler<DataAddedEventArgs> handler = (_, _) =>
            {
                foreach (var record in stream.ReadAll())
                    diagnostics.Add(Diagnostic(name, message(record)));
                if (diagnostics.ErrorCount >= RetentionPolicy.ErrorRecords)
                    capture.StopForLimit($"the {RetentionPolicy.ErrorRecords:N0}-error budget was reached");
            };
            stream.DataAdded += handler;
            unsubscribe.Add(() => stream.DataAdded -= handler);
        }
        EventHandler<DataAddedEventArgs> outputAdded = (_, _) => DrainOutput();
        output.DataAdded += outputAdded;
        Watch(shell.Streams.Error, "Error", record => record.ToString());
        Watch(shell.Streams.Warning, "Warning", record => record.Message);
        Watch(shell.Streams.Information, "Information", record => Display(record.MessageData));
        Watch(shell.Streams.Verbose, "Verbose", record => record.Message);
        Watch(shell.Streams.Debug, "Debug", record => record.Message);
        Watch(shell.Streams.Progress, "Progress", record => $"{record.Activity}: {record.StatusDescription} ({record.PercentComplete}%)");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invocation = shell.BeginInvoke<PSObject, PSObject>(null, output);
            using var registration = cancellationToken.Register(RequestStop);
            try
            {
                shell.EndInvoke(invocation);
            }
            finally
            {
                registration.Dispose();
                IAsyncResult? requested;
                lock (stopGate) requested = stop;
                if (requested is not null) shell.EndStop(requested);
            }
            DrainOutput();
            return new Invocation(retained, cancellationToken.IsCancellationRequested
                ? InvocationOutcome.Cancelled
                : diagnostics.ErrorCount > 0 || shell.HadErrors ? InvocationOutcome.CompletedWithErrors : InvocationOutcome.Completed);
        }
        catch (OperationCanceledException)
        {
            DrainOutput();
            return new Invocation(retained, InvocationOutcome.Cancelled);
        }
        catch (PipelineStoppedException exception)
        {
            if (!cancellationToken.IsCancellationRequested && !capture.LimitReached)
                diagnostics.Add(Diagnostic("Error", exception.Message));
            DrainOutput();
            return new Invocation(retained, cancellationToken.IsCancellationRequested ? InvocationOutcome.Cancelled : InvocationOutcome.Failed);
        }
        catch (RuntimeException exception)
        {
            if (!diagnostics.Snapshot().Any(record => record.Stream == "Error" && record.Message == exception.Message))
                diagnostics.Add(Diagnostic("Error", exception.Message));
            DrainOutput();
            return new Invocation(retained, cancellationToken.IsCancellationRequested ? InvocationOutcome.Cancelled : InvocationOutcome.Failed);
        }
        finally
        {
            output.DataAdded -= outputAdded;
            foreach (var detach in unsubscribe) detach();
            capture.RequestStop = null;
        }
    }

    private static void DisposeOutput(IEnumerable<PSObject> output)
    {
        foreach (var value in output.Select(value => value.BaseObject).Distinct(ReferenceEqualityComparer.Instance))
            if (value is IDisposable disposable) disposable.Dispose();
    }

    private void ConfigureShouldProcess(AutomationShell shell, IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var name in new[] { "Confirm", "WhatIf" })
        {
            if (!parameters.TryGetValue(name, out var value)) continue;
            if (!bool.TryParse(value, out var enabled))
                throw new ArgumentException($"{name} must be True or False.");
            var command = shell.Commands.Commands.Last();
            var metadata = command.IsScript ? null :
                runspace!.SessionStateProxy.InvokeCommand.GetCommand(command.CommandText, CommandTypes.Cmdlet | CommandTypes.Function);
            if (metadata?.Parameters.ContainsKey(name) != true)
                throw new ArgumentException($"This command does not support {name}.");
            shell.AddParameter(name, enabled);
        }
    }

    private static ConsoleRow Materialize(PSObject value, IReadOnlyList<ConsoleColumn> columns, StoredResult stored, ConsoleNode? node,
        int rowNumber, DisplayFailures failures)
    {
        var handle = Guid.NewGuid();
        var cells = new Dictionary<string, ConsoleCell>();
        foreach (var column in columns)
        {
            try { cells[column.Key] = ConsoleCell.From(SafeValue(CellValue(value, column.Key)), column.Kind); }
            catch (Exception exception)
            {
                cells[column.Key] = new ConsoleCell(null, "(unavailable)", PropertyError(exception));
                failures.Add(rowNumber, column.Key, exception);
            }
        }
        ProcessIdentity? identity = null;
        string? identityError = null;
        if (stored.Kind == ResourceKind.Processes && value.BaseObject is Process process)
        {
            try { identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime()); }
            catch (Exception exception)
            {
                identityError = PropertyError(exception);
                failures.Add(rowNumber, "Process.StartTime (identity)", exception);
            }
        }
        var name = cells.TryGetValue("Name", out var cell) ? Convert.ToString(cell.Value, CultureInfo.InvariantCulture) : null;
        stored.Rows.Add(handle, new LiveRow(value, name, identity, identityError));
        ConsoleNode? related = null;
        try { related = RelatedNode(value, node); }
        catch (Exception exception)
        {
            cells["Navigation"] = new ConsoleCell(null, "(unavailable)", PropertyError(exception));
            failures.Add(rowNumber, "Navigation", exception);
        }
        return new ConsoleRow(handle, cells, related);
    }

    private static object? CellValue(PSObject value, string key)
    {
        if (key == "Value" && value.Properties[key] is null) return value.BaseObject;
        if (value.BaseObject is Process process)
        {
            return key switch
            {
                "Name" => process.ProcessName,
                "Id" => process.Id,
                "CPU" => process.TotalProcessorTime.TotalSeconds,
                "WorkingSet" => process.WorkingSet64,
                "Threads" => process.Threads.Count,
                "Handles" => process.HandleCount,
                _ => value.Properties[key]?.Value
            };
        }
        if (value.BaseObject is NetworkInterface adapter)
        {
            return key switch
            {
                "Addresses" => string.Join(", ", adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address.ToString())),
                "MAC" => adapter.GetPhysicalAddress().ToString(),
                _ => value.Properties[key]?.Value
            };
        }
        if (OperatingSystem.IsWindows() && value.BaseObject is System.DirectoryServices.DirectoryEntry entry)
        {
            return key switch
            {
                "Name" => entry.Name,
                "ObjectClass" => entry.SchemaClassName,
                "PrincipalSource" => "ADSI",
                "SID" => entry.Properties["objectSid"].Value is byte[] sid
                    ? new System.Security.Principal.SecurityIdentifier(sid, 0).Value : null,
                _ => value.Properties[key]?.Value
            };
        }
        if (value.BaseObject is PSDriveInfo drive)
        {
            if (key == "Provider") return drive.Provider.Name;
            if (key is "Used" or "Free" && drive.Provider.Name != "FileSystem") return null;
        }
        if (key == "BaseAddress" && value.BaseObject is ProcessModule module) return $"0x{module.BaseAddress.ToInt64():X}";
        if (key == "Name" && value.BaseObject is System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
            return certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false);
        if (key == "Name") return value.Properties["Name"]?.Value ?? value.Properties["PSChildName"]?.Value;
        return value.Properties[key]?.Value;
    }

    private static ConsoleNode? RelatedNode(PSObject value, ConsoleNode? node)
    {
        if (node is null) return null;
        var name = Convert.ToString(value.Properties["PSChildName"]?.Value ?? value.Properties["Name"]?.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        switch (node.Kind)
        {
            case ResourceKind.Drives when value.BaseObject is PSDriveInfo drive:
                return Child(drive.Name, ResourceKind.ProviderPath, $"{drive.Name}:\\", drive.Provider.Name == "Registry", drive.Provider.Name);
            case ResourceKind.Registry:
                return Child(name, ResourceKind.ProviderPath, (string)value.Properties["Path"].Value, true, "Registry");
            case ResourceKind.ProviderPath:
                if (value.Properties["PSIsContainer"]?.Value is true)
                    return Child(name, ResourceKind.ProviderPath, Convert.ToString(value.Properties["PSPath"]?.Value, CultureInfo.InvariantCulture),
                        node.WindowsOnly, node.ProviderName);
                break;
            case ResourceKind.EventLogs:
                var log = Convert.ToString(value.Properties["LogName"]?.Value, CultureInfo.InvariantCulture);
                return Child(log ?? "Event log", ResourceKind.EventEntries, log, true);
            case ResourceKind.LocalGroups:
                return Child(name, ResourceKind.GroupMembers, name, true);
            case ResourceKind.WmiNamespaces:
                var type = Convert.ToString(value.Properties["EntryType"]?.Value, CultureInfo.InvariantCulture);
                var path = Convert.ToString(value.Properties["Namespace"]?.Value, CultureInfo.InvariantCulture);
                return type == "Classes"
                    ? Child("Classes", ResourceKind.WmiClasses, path, true)
                    : Child(name, ResourceKind.WmiNamespaces, path, true);
            case ResourceKind.WmiClasses:
                var className = Convert.ToString(value.Properties["CimClassName"]?.Value, CultureInfo.InvariantCulture);
                return Child(className ?? "Class", ResourceKind.WmiInstances, $"{node.Path}|{className}", true);
        }
        return null;
    }

    private static ConsoleNode Child(string name, ResourceKind kind, string? path, bool windows = false, string? provider = null) =>
        new($"{kind}:{path}", name, kind, $"Browse {name}.", path, windows, provider);

    private static void ConfigureAction(AutomationShell shell, ConsoleActionId action, LiveRow row,
        IReadOnlyDictionary<string, string> parameters)
    {
        switch (action)
        {
            case ConsoleActionId.StopProcess:
                // InputObject preserves the retained Process object rather than resolving a fresh PID.
                shell.AddCommand("Stop-Process").AddParameter("InputObject", OriginalProcess(row, forStop: true))
                    .AddParameter("ErrorAction", ActionPreference.Stop);
                break;
            case ConsoleActionId.ProcessModules:
            case ConsoleActionId.ProcessThreads:
                shell.AddScript(action == ConsoleActionId.ProcessModules
                        ? "param([Diagnostics.Process] $Process) $ErrorActionPreference = 'Stop'; $Process.Modules"
                        : "param([Diagnostics.Process] $Process) $ErrorActionPreference = 'Stop'; $Process.Threads", useLocalScope: true)
                    .AddParameter("Process", OriginalProcess(row));
                break;
            case ConsoleActionId.SetProcessPriority:
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Process priority changes are available only on Windows.");
                var priority = Required(parameters, "Priority").ToUpperInvariant() switch
                {
                    "IDLE" => ProcessPriorityClass.Idle,
                    "BELOWNORMAL" => ProcessPriorityClass.BelowNormal,
                    "NORMAL" => ProcessPriorityClass.Normal,
                    "ABOVENORMAL" => ProcessPriorityClass.AboveNormal,
                    "HIGH" => ProcessPriorityClass.High,
                    _ => throw new ArgumentException("Priority must be Idle, BelowNormal, Normal, AboveNormal, or High. RealTime is not allowed.")
                };
                shell.AddScript("param([Diagnostics.Process] $Process, [Diagnostics.ProcessPriorityClass] $Priority) $ErrorActionPreference = 'Stop'; $Process.PriorityClass = $Priority", useLocalScope: true)
                    .AddParameter("Process", OriginalProcess(row)).AddParameter("Priority", priority);
                break;
            case ConsoleActionId.StartService:
            case ConsoleActionId.StopService:
            case ConsoleActionId.RestartService:
                shell.AddCommand(action switch
                {
                    ConsoleActionId.StartService => "Start-Service",
                    ConsoleActionId.StopService => "Stop-Service",
                    _ => "Restart-Service"
                }).AddParameter("Name", WildcardPattern.Escape(row.Name!)).AddParameter("ErrorAction", ActionPreference.Stop);
                break;
            case ConsoleActionId.RemoveDrive:
                shell.AddCommand("Remove-PSDrive").AddParameter("Name", WildcardPattern.Escape(row.Name!))
                    .AddParameter("Scope", "Global").AddParameter("ErrorAction", ActionPreference.Stop);
                break;
            case ConsoleActionId.SetValue:
                shell.AddCommand("Set-Item").AddParameter("LiteralPath", $"Env:\\{row.Name}")
                    .AddParameter("Value", parameters.TryGetValue("Value", out var value) ? value : string.Empty)
                    .AddParameter("ErrorAction", ActionPreference.Stop);
                break;
            case ConsoleActionId.RemoveItem:
                shell.AddCommand("Remove-Item").AddParameter("LiteralPath", $"Env:\\{row.Name}").AddParameter("ErrorAction", ActionPreference.Stop);
                break;
            default: throw new ArgumentException("This action is not a built-in runtime mutation.", nameof(action));
        }
    }

    private static Process OriginalProcess(LiveRow row, bool forStop = false)
    {
        if (row.Identity is not { } identity)
            throw new InvalidOperationException($"Cannot verify the original process identity: {row.IdentityError ?? "start time unavailable"}.");
        if (forStop && identity.Id == Environment.ProcessId)
            throw new InvalidOperationException("The console cannot stop its own process.");
        if (row.Value.BaseObject is not Process original || original.HasExited)
            throw new InvalidOperationException("The original process has exited. Refresh the view.");
        using var current = Process.GetProcessById(identity.Id);
        if (current.StartTime.ToUniversalTime() != identity.StartTime)
            throw new InvalidOperationException("The process ID has been reused. Refresh the view.");
        return original;
    }

    private static void ValidateAction(ConsoleActionId action, ResourceKind kind, int count)
    {
        var expected = action switch
        {
            ConsoleActionId.StopProcess or ConsoleActionId.StartProcess or ConsoleActionId.SetProcessPriority
                or ConsoleActionId.ProcessModules or ConsoleActionId.ProcessThreads => ResourceKind.Processes,
            ConsoleActionId.StartService or ConsoleActionId.StopService or ConsoleActionId.RestartService => ResourceKind.Services,
            ConsoleActionId.AddDrive or ConsoleActionId.RemoveDrive => ResourceKind.Drives,
            ConsoleActionId.SetValue or ConsoleActionId.RemoveItem => ResourceKind.Environment,
            _ => throw new ArgumentException("This action is handled by the desktop, not the runtime.", nameof(action))
        };
        if (expected != kind) throw new ArgumentException("This action does not apply to the selected result.");
        if (action is ConsoleActionId.SetProcessPriority or ConsoleActionId.SetValue && count != 1)
            throw new ArgumentException("Select exactly one object for this action.");
        if (action is not (ConsoleActionId.AddDrive or ConsoleActionId.StartProcess) && count == 0)
            throw new ArgumentException("Select at least one object.");
        if (action is ConsoleActionId.ProcessModules or ConsoleActionId.ProcessThreads && count != 1)
            throw new ArgumentException("Select exactly one process for a related view.");
    }

    private static string Required(IReadOnlyDictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"The {name} parameter is required.");

    private static object? SafeValue(object? value)
    {
        if (value is PSObject psObject) return SafeValue(psObject.BaseObject);
        return value switch
        {
            null => null,
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime => value,
            _ => Display(value)
        };
    }

    private static string Display(object? value)
    {
        if (value is null) return "(null)";
        if (value is PSObject wrapper) return Display(wrapper.BaseObject);
        if (value is IEnumerable values && value is not string)
        {
            var preview = values.Cast<object?>().Take(101).ToArray();
            return string.Join(", ", preview.Take(100).Select(Display)) + (preview.Length > 100 ? ", … (additional items)" : string.Empty);
        }
        return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private static string PropertyError(Exception exception) => exception.GetBaseException().Message;
    private static DiagnosticRecord Diagnostic(string stream, string message) => new(DateTimeOffset.Now, stream, message);
    private static bool RequiresWindows(ResourceKind kind) => kind is ResourceKind.Services or ResourceKind.EventLogs
        or ResourceKind.EventEntries or ResourceKind.Registry or ResourceKind.Shares or ResourceKind.LocalUsers
        or ResourceKind.LocalGroups or ResourceKind.GroupMembers or ResourceKind.WmiNamespaces
        or ResourceKind.WmiClasses or ResourceKind.WmiInstances;

    private static void EnsureSuccessful(ConsoleResult result)
    {
        if (result.Outcome == InvocationOutcome.Cancelled) throw new OperationCanceledException("The session operation was cancelled.");
        if (result.Outcome != InvocationOutcome.Completed)
            throw new InvalidOperationException($"{result.Outcome}: " +
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)
                    .Append(result.RetentionNotice ?? string.Empty)));
    }

    private static string ActionDescription(ConsoleActionId action, IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<LiveRow> selected, IReadOnlyList<Guid> handles)
    {
        string Parameter(string name) => parameters.TryGetValue(name, out var value) ? value : string.Empty;
        var options = string.Concat(new[] { "Confirm", "WhatIf" }
            .Where(name => parameters.TryGetValue(name, out var value) && bool.TryParse(value, out _))
            .Select(name => $" -{name}:${bool.Parse(parameters[name]).ToString().ToLowerInvariant()}"));
        string Each(Func<LiveRow, string> format) => selected.Count > 0
            ? string.Join(Environment.NewLine, selected.Select(row => format(row) + options))
            : $"# Session-dependent {action}; selected handles: {string.Join(", ", handles)}";
        var processContext = $"# Session-dependent: retained live process objects; original Ids: {string.Join(", ", selected.Select(row => row.Identity?.Id.ToString(CultureInfo.InvariantCulture) ?? "unavailable"))}; handles: {string.Join(", ", handles)}";
        var description = action switch
        {
            ConsoleActionId.AddDrive => PowerShellDisplay.Command("New-PSDrive", ("Name", Parameter("Name")),
                ("PSProvider", Parameter("Provider")), ("Root", Parameter("Root")), ("Scope", "Global"), ("ErrorAction", "Stop")),
            ConsoleActionId.StartProcess => PowerShellDisplay.Command("Start-Process", ("FilePath", Parameter("FilePath")))
                + (string.IsNullOrWhiteSpace(Parameter("Arguments")) ? string.Empty
                    : PowerShellDisplay.Arguments([("ArgumentList", HistoryArguments(Parameter("Arguments")))]))
                + " -PassThru -ErrorAction 'Stop'",
            ConsoleActionId.StopProcess => processContext + Environment.NewLine + "Stop-Process -InputObject $selectedProcesses -ErrorAction 'Stop'",
            ConsoleActionId.ProcessModules => processContext + Environment.NewLine + "$selectedProcess.Modules",
            ConsoleActionId.ProcessThreads => processContext + Environment.NewLine + "$selectedProcess.Threads",
            ConsoleActionId.SetProcessPriority => processContext + Environment.NewLine
                + "$selectedProcess.PriorityClass = [Diagnostics.ProcessPriorityClass] " + PowerShellDisplay.Literal(Parameter("Priority")),
            ConsoleActionId.RemoveDrive => Each(row => PowerShellDisplay.Command("Remove-PSDrive",
                ("Name", WildcardPattern.Escape(row.Name!)), ("Scope", "Global"), ("ErrorAction", "Stop"))),
            ConsoleActionId.StartService or ConsoleActionId.StopService or ConsoleActionId.RestartService => Each(row =>
                PowerShellDisplay.Command(action switch
                {
                    ConsoleActionId.StartService => "Start-Service",
                    ConsoleActionId.StopService => "Stop-Service",
                    _ => "Restart-Service"
                }, ("Name", WildcardPattern.Escape(row.Name!)), ("ErrorAction", "Stop"))),
            ConsoleActionId.SetValue => Each(row => PowerShellDisplay.Command("Set-Item", ("LiteralPath", $"Env:\\{row.Name}"),
                ("Value", SensitiveName(row.Name) ? "<REDACTED>" : Parameter("Value")), ("ErrorAction", "Stop"))),
            ConsoleActionId.RemoveItem => Each(row => PowerShellDisplay.Command("Remove-Item",
                ("LiteralPath", $"Env:\\{row.Name}"), ("ErrorAction", "Stop"))),
            _ => $"# Session-dependent action: {action}; handles: {string.Join(", ", handles)}"
        };
        if (action is not (ConsoleActionId.RemoveDrive or ConsoleActionId.StartService or ConsoleActionId.StopService
            or ConsoleActionId.RestartService or ConsoleActionId.SetValue or ConsoleActionId.RemoveItem))
            description += options;
        return description;
    }

    private static string HistoryArguments(string arguments) => arguments.Trim() is "--version" or "--info" or "--help" or "-h" or "-?" or "/?"
        ? arguments : "<REDACTED: arguments may contain secrets>";

    private static bool SensitiveName(string? name) => name is not null && new[]
    {
        "PASSWORD", "PASSWD", "TOKEN", "SECRET", "CREDENTIAL", "KEY", "CONNECTIONSTRING", "AUTHORIZATION"
    }.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private ResultLease AcquireResult(Guid resultId)
    {
        lock (resultLock)
        {
            if (!results.TryGetValue(resultId, out var result))
                throw new InvalidOperationException("This result has been released or is no longer available. Refresh the view before making changes.");
            result.Readers++;
            return new ResultLease(this, result);
        }
    }

    private sealed class ResultLease(PowerShellSession owner, StoredResult result) : IDisposable
    {
        public StoredResult Result { get; } = result;

        public void Dispose()
        {
            lock (owner.resultLock)
            {
                Result.Readers--;
                if (Result.Released && Result.Readers == 0) Result.DisposeObjects();
            }
        }
    }

    private sealed record Invocation(IReadOnlyList<PSObject> Output, InvocationOutcome Outcome);
    private sealed record ProcessIdentity(int Id, DateTime StartTime);
    private sealed record LiveRow(PSObject Value, string? Name, ProcessIdentity? Identity, string? IdentityError);
    private sealed class InvocationCapture(Stopwatch stopwatch)
    {
        public Stopwatch Stopwatch { get; } = stopwatch;
        public TimeSpan? FirstOutputLatency { get; set; }
        public long OutputReceived { get; set; }
        public int PeakOutputBuffer { get; set; }
        public HashSet<object> RetainedObjects { get; } = new(ReferenceEqualityComparer.Instance);
        public bool LimitReached { get; private set; }
        public string? LimitReason { get; private set; }
        public Action? RequestStop { get; set; }

        public void StopForLimit(string reason)
        {
            LimitReached = true;
            LimitReason ??= reason;
            RequestStop?.Invoke();
        }
    }
    private sealed class DisplayFailures(DiagnosticBuffer diagnostics)
    {
        private const int Limit = 20;
        public int Count { get; private set; }

        public void Add(int rowNumber, string property, Exception exception)
        {
            Count++;
            if (Count <= Limit)
                diagnostics.Add(Diagnostic("Error", $"Display property '{property}' failed on row {rowNumber}: {PropertyError(exception)}"));
        }

        public void Summarize()
        {
            if (Count > Limit)
                diagnostics.Add(Diagnostic("Error", $"Display property evaluation failed {Count} times; first {Limit} failures shown, {Count - Limit} additional failures omitted. Individual cells retain their errors."));
        }
    }
    private sealed class DefaultRunspaceScope : IDisposable
    {
        private readonly System.Management.Automation.Runspaces.Runspace? previous =
            System.Management.Automation.Runspaces.Runspace.DefaultRunspace;

        public DefaultRunspaceScope(System.Management.Automation.Runspaces.Runspace? current) =>
            System.Management.Automation.Runspaces.Runspace.DefaultRunspace = current;

        public void Dispose() => System.Management.Automation.Runspaces.Runspace.DefaultRunspace = previous;
    }

    private sealed class StoredResult(ResourceKind kind)
    {
        public ResourceKind Kind { get; } = kind;
        public Dictionary<Guid, LiveRow> Rows { get; } = [];
        public int Readers { get; set; }
        public bool Released { get; set; }

        public void DisposeObjects()
        {
            foreach (var value in Rows.Values.Select(row => row.Value.BaseObject).Distinct(ReferenceEqualityComparer.Instance))
                if (value is IDisposable disposable) disposable.Dispose();
            Rows.Clear();
        }
    }
}
