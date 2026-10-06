using System.Diagnostics;
using System.Net.NetworkInformation;
using Runspace.Core;
using Runspace.PowerShell;

namespace Runspace.Tests;

public sealed class PowerShellSessionTests
{
    private static ConsoleNode Node(ResourceKind kind) =>
        BuiltInCatalog.LocalSystem.Single(node => node.Kind == kind);

    [Fact]
    public async Task ResolvesActualProvidersForPathsWithoutUiMetadata()
    {
        await using var session = new PowerShellSession();
        var key = "RUNSPACE_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key, "actual-provider-value");
        try
        {
            var environment = await session.QueryAsync(new ConsoleNode("env-direct", "Environment path",
                ResourceKind.ProviderPath, "Direct provider path", "Env:"));
            AssertRead(environment);
            Assert.Equal(["Name", "Value"], environment.Columns.Select(column => column.Key));
            Assert.Contains(environment.Diagnostics, record => record.Message.Contains("Source provider: Environment; drive: Env"));
            var row = environment.Rows.FirstOrDefault(row => row.Cells["Name"].Display == key);
            Assert.NotNull(row);
            Assert.Equal("actual-provider-value", row.Cells["Value"].Value);
            var aliases = await session.QueryAsync(new ConsoleNode("alias-direct", "Aliases", ResourceKind.ProviderPath,
                "Actual provider overrides UI guesses", "Alias:", WindowsOnly: true, ProviderName: "FileSystem"));
            AssertRead(aliases);
            Assert.Equal(["Name", "Definition", "Options"], aliases.Columns.Select(column => column.Key));
            Assert.Contains(aliases.Diagnostics, record => record.Message.Contains("Source provider: Alias"));
            var variables = await session.QueryAsync(new ConsoleNode("variable-direct", "Variables", ResourceKind.ProviderPath,
                "Actual variable provider", "Variable:"));
            AssertRead(variables);
            Assert.Equal(["Name", "Value", "Options"], variables.Columns.Select(column => column.Key));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public async Task ResolvesCertificateProviderColumnsAndContainerMetadata()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var session = new PowerShellSession();
        var root = await session.QueryAsync(new ConsoleNode("cert-direct", "Certificates", ResourceKind.ProviderPath,
            "Built-in certificate provider", "Cert:"));
        AssertRead(root);
        Assert.Equal(["Name", "Subject", "Thumbprint", "NotBefore", "NotAfter", "HasPrivateKey"],
            root.Columns.Select(column => column.Key));
        Assert.Contains(root.Diagnostics, record => record.Message.Contains("Source provider: Certificate; drive: Cert"));
        var currentUser = Assert.Single(root.Rows, row => row.Cells["Name"].Display == "CurrentUser");
        Assert.NotNull(currentUser.RelatedNode);
        Assert.Equal("Certificate", currentUser.RelatedNode.ProviderName);
        var stores = await session.QueryAsync(currentUser.RelatedNode);
        AssertRead(stores);
        var trustedRoots = Assert.Single(stores.Rows, row => row.Cells["Name"].Display == "Root");
        Assert.Equal("Certificate", trustedRoots.RelatedNode!.ProviderName);
        var certificates = await session.QueryAsync(trustedRoots.RelatedNode);
        AssertRead(certificates);
        Assert.All(certificates.Rows, row => Assert.NotEqual("(null)", row.Cells["Name"].Display));
    }

    [Fact]
    public void DescribesQueriesUsingQuotedPowerShellLiteralsAndNativeExpressions()
    {
        const string path = "D:\\O'Brien [literal];$(Write-Output injected)\\line\nnext";
        var provider = QueryDefinition.For(new ConsoleNode("literal", "Literal", ResourceKind.ProviderPath, "Literal", path, ProviderName: "FileSystem"));
        Assert.Equal("Get-ChildItem -LiteralPath 'D:\\O''Brien [literal];$(Write-Output injected)\\line\nnext'", provider.Description);
        var log = QueryDefinition.For(new ConsoleNode("log", "Log", ResourceKind.EventEntries, "Log", "O'Brien;$(oops)"));
        Assert.Equal("Get-WinEvent -LogName 'O''Brien;$(oops)' -MaxEvents 200", log.Description);
        Assert.Equal("[System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()",
            QueryDefinition.For(Node(ResourceKind.NetworkProperties)).Description);
        Assert.Equal("[System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()",
            QueryDefinition.For(Node(ResourceKind.NetworkInterfaces)).Description);
        System.Management.Automation.Language.Parser.ParseInput(provider.Description, out _, out var errors);
        Assert.Empty(errors);
        foreach (var node in BuiltInCatalog.LocalSystem)
        {
            System.Management.Automation.Language.Parser.ParseInput(QueryDefinition.For(node).Description, out _, out var queryErrors);
            Assert.Empty(queryErrors);
        }
        const string smartQuotes = "‘curly’ and 'ascii' $(not-code)";
        var ast = System.Management.Automation.Language.Parser.ParseInput(PowerShellDisplay.Literal(smartQuotes), out _, out var quoteErrors);
        Assert.Empty(quoteErrors);
        var literal = Assert.Single(ast.FindAll(node => node is System.Management.Automation.Language.StringConstantExpressionAst, true));
        Assert.Equal(smartQuotes, ((System.Management.Automation.Language.StringConstantExpressionAst)literal).Value);
    }

    [Fact]
    public async Task ReportsBoundedDisplayPropertyFailuresWithoutLosingLiveObjects()
    {
        await using var session = new PowerShellSession();
        var result = await session.InvokeForTestingAsync("""
            1..50 | ForEach-Object {
                [pscustomobject]@{ Name = "object-$_" } |
                    Add-Member -MemberType ScriptProperty -Name Value -Value { throw 'display-getter-failure' } -PassThru
            }
            """);
        Assert.Equal(InvocationOutcome.CompletedWithErrors, result.Outcome);
        Assert.Equal(50, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Contains("display-getter-failure", row.Cells["Value"].Error));
        Assert.Contains(result.Diagnostics, record => record.Stream == "Error" && record.Message.Contains("Value") && record.Message.Contains("display-getter-failure"));
        Assert.Contains(result.Diagnostics, record => record.Message.Contains("50") && record.Message.Contains("omitted"));
        Assert.InRange(result.Diagnostics.Count, 1, 21);
        var inspection = await session.InspectAsync(result.Id, result.Rows[0].Handle);
        Assert.Contains(inspection, property => property.Name == "Value" && property.Error != null && property.Error.Contains("display-getter-failure"));
        Assert.Contains(inspection, property => property.Name == "Name" && property.Value == "object-1");
        var failed = await session.InvokeForTestingAsync("""
            [pscustomobject]@{ Name = 'partial' } |
                Add-Member -MemberType ScriptProperty -Name Value -Value { throw 'display-getter-failure' } -PassThru
            throw 'pipeline-failure'
            """);
        Assert.Equal(InvocationOutcome.Failed, failed.Outcome);
        Assert.Contains("display-getter-failure", Assert.Single(failed.Rows).Cells["Value"].Error);
        Assert.Contains(failed.Diagnostics, record => record.Message.Contains("pipeline-failure"));
    }

    [Fact]
    public async Task RedactsSensitiveHistoryValuesWithoutChangingBoundParameters()
    {
        await using var session = new PowerShellSession();
        var key = "RUNSPACE_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key, "before");
        try
        {
            var environment = await session.QueryAsync(Node(ResourceKind.Environment));
            var row = Assert.Single(environment.Rows, row => row.Cells["Name"].Display == key);
            const string value = "synthetic-test-secret' $(not-code)";
            var edit = await session.ExecuteAsync(ConsoleActionId.SetValue, environment.Id, [row.Handle],
                new Dictionary<string, string> { ["Value"] = value });
            AssertCompleted(edit);
            Assert.Contains("-LiteralPath " + PowerShellDisplay.Literal($"Env:\\{key}"), edit.Script);
            Assert.Contains("<REDACTED>", edit.Script);
            Assert.DoesNotContain(value, edit.Script);
            Assert.Equal(value, Environment.GetEnvironmentVariable(key));
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public async Task RejectsMultipleSelectionsForSingleObjectMutations()
    {
        await using var session = new PowerShellSession();
        var keys = new[] { "RUNSPACE_TEST_" + Guid.NewGuid().ToString("N"), "RUNSPACE_TEST_" + Guid.NewGuid().ToString("N") };
        foreach (var key in keys) Environment.SetEnvironmentVariable(key, "unchanged");
        try
        {
            var environment = await session.QueryAsync(Node(ResourceKind.Environment));
            var selection = environment.Rows.Where(row => keys.Contains(row.Cells["Name"].Display)).Select(row => row.Handle).ToArray();
            Assert.Equal(2, selection.Length);
            var rejected = await session.ExecuteAsync(ConsoleActionId.SetValue, environment.Id, selection,
                new Dictionary<string, string> { ["Value"] = "must-not-be-applied" });
            Assert.Equal(InvocationOutcome.Failed, rejected.Outcome);
            Assert.Contains(rejected.Diagnostics, record => record.Message.Contains("exactly one"));
            var duplicate = await session.ExecuteAsync(ConsoleActionId.SetValue, environment.Id, [selection[0], selection[0]],
                new Dictionary<string, string> { ["Value"] = "must-not-be-applied" });
            Assert.Equal(InvocationOutcome.Failed, duplicate.Outcome);
            Assert.All(keys, key => Assert.Equal("unchanged", Environment.GetEnvironmentVariable(key)));
            var processes = await session.QueryAsync(Node(ResourceKind.Processes));
            // An unknown second handle ensures a broken guard cannot mutate another process.
            var priorities = await session.ExecuteAsync(ConsoleActionId.SetProcessPriority, processes.Id,
                [processes.Rows[0].Handle, Guid.NewGuid()], new Dictionary<string, string> { ["Priority"] = "Normal" });
            Assert.Equal(InvocationOutcome.Failed, priorities.Outcome);
            Assert.Contains(priorities.Diagnostics, record => record.Message.Contains("exactly one"));
        }
        finally
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task QueriesRealProcessesAndInspectsRetainedObjects()
    {
        await using var session = new PowerShellSession();
        Assert.StartsWith("7.6.", session.RuntimeVersion);
        var result = await session.QueryAsync(Node(ResourceKind.Processes));
        AssertRead(result);
        Assert.Equal(["Name", "Id", "CPU", "WorkingSet", "Threads", "Handles"], result.Columns.Select(column => column.Key));
        var current = Assert.Single(result.Rows, row => Convert.ToInt32(row.Cells["Id"].Value) == Environment.ProcessId);
        Assert.IsType<double>(current.Cells["CPU"].Value);
        Assert.IsType<long>(current.Cells["WorkingSet"].Value);
        var properties = await session.InspectAsync(result.Id, current.Handle);
        Assert.Contains(properties, property => property.Name == "Id" && property.Value == Environment.ProcessId.ToString());
        Assert.Contains(properties, property => property.Name == "ProcessName" && property.Error is null);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.Contains("Get-Process", result.Script);

        var rejected = await session.ExecuteAsync(ConsoleActionId.StopProcess, result.Id, [current.Handle], new Dictionary<string, string>());
        Assert.Equal(InvocationOutcome.Failed, rejected.Outcome);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Message.Contains("own process"));
        session.ReleaseResult(result.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InspectAsync(result.Id, current.Handle));
    }

    [Fact]
    public async Task ImportsManagementModuleFromDeployedSdkAssets()
    {
        await using var session = new PowerShellSession();
        AssertRead(await session.QueryAsync(Node(ResourceKind.Processes)));
        var module = await session.InvokeForTestingAsync("Get-Module Microsoft.PowerShell.Management | ForEach-Object ModuleBase");
        AssertCompleted(module);
        var path = Assert.IsType<string>(Assert.Single(module.Rows).Cells["Value"].Value);
        Assert.StartsWith(AppContext.BaseDirectory, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var manifest = Path.Combine(AppContext.BaseDirectory, "runtimes", OperatingSystem.IsWindows() ? "win" : "unix",
            "lib", "net10.0", "Modules", "Microsoft.PowerShell.Management", "Microsoft.PowerShell.Management.psd1");
        Assert.True(File.Exists(manifest), $"Missing deployed SDK module manifest: {manifest}");
        var implementation = await session.InvokeForTestingAsync("Get-Command Get-Process | ForEach-Object { $_.ImplementingType.Assembly.Location }");
        AssertCompleted(implementation);
        var assembly = Assert.IsType<string>(Assert.Single(implementation.Rows).Cells["Value"].Value);
        Assert.StartsWith(AppContext.BaseDirectory, assembly, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
            AssertCompleted(await session.InvokeForTestingAsync("Get-Command Get-Service, Get-WinEvent, Get-CimInstance, Get-CimClass"));
    }

    [Fact]
    public async Task QueriesProcessModulesAndThreadsAsLiveRelatedViews()
    {
        await using var session = new PowerShellSession();
        var processes = await session.QueryAsync(Node(ResourceKind.Processes));
        AssertRead(processes);
        var selected = Assert.Single(processes.Rows, row => Convert.ToInt32(row.Cells["Id"].Value) == Environment.ProcessId);
        foreach (var kind in new[] { ResourceKind.ProcessModules, ResourceKind.ProcessThreads })
        {
            var node = new ConsoleNode("related", "Related process view", kind, "Read-only related view", Environment.ProcessId.ToString());
            var queried = await session.QueryAsync(node);
            AssertRead(queried);
            Assert.NotEmpty(queried.Rows);
            Assert.NotEmpty(await session.InspectAsync(queried.Id, queried.Rows[0].Handle));
            var action = kind == ResourceKind.ProcessModules ? ConsoleActionId.ProcessModules : ConsoleActionId.ProcessThreads;
            var executed = await session.ExecuteAsync(action, processes.Id, [selected.Handle], new Dictionary<string, string>());
            AssertRead(executed);
            Assert.NotEmpty(executed.Rows);
            Assert.Equal(queried.Columns.Select(column => column.Key), executed.Columns.Select(column => column.Key));
            Assert.Contains("Session-dependent", executed.Script);
            Assert.Contains(Environment.ProcessId.ToString(), executed.Script);
        }
        var invalid = await session.QueryAsync(new ConsoleNode("invalid", "Invalid", ResourceKind.ProcessModules, "Invalid process Id", "1; throw 'injected'"));
        Assert.Equal(InvocationOutcome.Failed, invalid.Outcome);
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Message.Contains("numeric process Id"));
    }

    [Fact]
    public async Task ReportsOnlyTheLocalManagedComputer()
    {
        await using var session = new PowerShellSession();
        var result = await session.QueryAsync(new ConsoleNode("local", "Managed Computers", ResourceKind.ManagedComputers, "Local machine"));
        AssertCompleted(result);
        var row = Assert.Single(result.Rows);
        Assert.Equal(Environment.MachineName, row.Cells["Name"].Value);
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSDescription, row.Cells["OS"].Value);
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), row.Cells["Architecture"].Value);
    }

    [Fact]
    public async Task StartsAHarmlessProcessAndRejectsUnsafePriorityParameters()
    {
        await using var session = new PowerShellSession();
        var processes = await session.QueryAsync(Node(ResourceKind.Processes));
        AssertRead(processes);
        var selected = Assert.Single(processes.Rows, row => Convert.ToInt32(row.Cells["Id"].Value) == Environment.ProcessId);
        using var current = Process.GetCurrentProcess();
        var priorityBefore = OperatingSystem.IsWindows() ? current.PriorityClass : ProcessPriorityClass.Normal;
        foreach (var priority in new[] { "RealTime", "128", "High; throw 'injected'" })
        {
            var rejected = await session.ExecuteAsync(ConsoleActionId.SetProcessPriority, processes.Id, [selected.Handle],
                new Dictionary<string, string> { ["Priority"] = priority });
            Assert.Equal(InvocationOutcome.Failed, rejected.Outcome);
            Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Stream == "Error"
                && diagnostic.Message.Contains(OperatingSystem.IsWindows() ? "Priority must be" : "Windows"));
        }
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(priorityBefore, current.PriorityClass);
            if (priorityBefore != ProcessPriorityClass.RealTime)
            {
                // Reapply the existing priority, rather than changing the test host's scheduling.
                AssertCompleted(await session.ExecuteAsync(ConsoleActionId.SetProcessPriority, processes.Id, [selected.Handle],
                    new Dictionary<string, string> { ["Priority"] = priorityBefore.ToString() }));
                Assert.Equal(priorityBefore, current.PriorityClass);
            }
        }
        var started = await session.ExecuteAsync(ConsoleActionId.StartProcess, processes.Id, [],
            new Dictionary<string, string> { ["FilePath"] = Environment.ProcessPath!, ["Arguments"] = "--version" });
        AssertRead(started);
        Assert.Contains("-FilePath " + PowerShellDisplay.Literal(Environment.ProcessPath), started.Script);
        Assert.Contains("-ArgumentList '--version'", started.Script);
        Assert.Single(started.Rows);
        var startedId = Convert.ToInt32(started.Rows[0].Cells["Id"].Value);
        Assert.NotEqual(Environment.ProcessId, startedId);
        try
        {
            using var child = Process.GetProcessById(startedId);
            Assert.True(child.WaitForExit(15_000), "The harmless dotnet --version process did not exit.");
        }
        catch (ArgumentException) { /* The short-lived process has already exited. */ }
        session.ReleaseResult(processes.Id);
        var stale = await session.ExecuteAsync(ConsoleActionId.StartProcess, processes.Id, [],
            new Dictionary<string, string> { ["FilePath"] = Environment.ProcessPath!, ["Arguments"] = "--version" });
        Assert.Equal(InvocationOutcome.Failed, stale.Outcome);
    }

    [Fact]
    public async Task DiscoversActualProvidersAndCreatesLiteralSessionDrive()
    {
        await using var session = new PowerShellSession();
        var providers = await session.GetProvidersAsync();
        Assert.Contains(providers, provider => provider.Name == "FileSystem");
        Assert.Contains(providers, provider => provider.Name == "Environment");
        Assert.NotEmpty(await session.GetDriveNodesAsync());

        var name = "Test" + Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".runtime-tests", name, "quoted'[bracket];$(not-code)"));
        Directory.CreateDirectory(Path.Combine(root, "child[one]"));
        await File.WriteAllTextAsync(Path.Combine(root, "file' [one].txt"), "runtime-test");
        var drives = await session.QueryAsync(Node(ResourceKind.Drives));
        AssertCompleted(drives);
        foreach (var row in drives.Rows.Where(row => row.Cells["Provider"].Display != "FileSystem"))
        {
            Assert.Null(row.Cells["Used"].Value);
            Assert.Null(row.Cells["Used"].Error);
            Assert.Null(row.Cells["Free"].Value);
            Assert.Null(row.Cells["Free"].Error);
        }
        ConsoleResult? createdDrives = null;
        try
        {
            var add = await session.ExecuteAsync(ConsoleActionId.AddDrive, drives.Id, [],
                new Dictionary<string, string> { ["Name"] = name, ["Provider"] = "FileSystem", ["Root"] = root });
            AssertCompleted(add);
            Assert.Contains("-Root " + PowerShellDisplay.Literal(root), add.Script);
            Assert.Contains("-Name " + PowerShellDisplay.Literal(name), add.Script);
            createdDrives = await session.QueryAsync(Node(ResourceKind.Drives));
            AssertCompleted(createdDrives);
            var row = Assert.Single(createdDrives.Rows, row => row.Cells["Name"].Display == name);
            Assert.Equal("FileSystem", row.Cells["Provider"].Value);
            Assert.Equal(root, row.Cells["Root"].Value);
            Assert.NotNull(row.RelatedNode);
            var children = await session.QueryAsync(row.RelatedNode!);
            AssertCompleted(children);
            Assert.Contains(children.Rows, child => child.Cells["Name"].Display == "file' [one].txt");
            var folder = Assert.Single(children.Rows, child => child.Cells["Name"].Display == "child[one]");
            Assert.NotNull(folder.RelatedNode);
            Assert.Equal("FileSystem", folder.RelatedNode.ProviderName);
            AssertCompleted(await session.QueryAsync(folder.RelatedNode!));
            var literal = await session.QueryAsync(new ConsoleNode("literal", "Literal", ResourceKind.ProviderPath, "Literal path", root));
            AssertCompleted(literal);
            Assert.Contains(literal.Rows, child => child.Cells["Name"].Display == "file' [one].txt");
            Assert.Contains(literal.Columns, column => column.Key == "Length");
            Assert.Contains(literal.Diagnostics, record => record.Message.Contains("Source provider: FileSystem"));

            var remove = await session.ExecuteAsync(ConsoleActionId.RemoveDrive, createdDrives.Id, [row.Handle], new Dictionary<string, string>());
            AssertCompleted(remove);
            Assert.DoesNotContain(await session.GetDriveNodesAsync(), node => node.Path == $"{name}:\\");
            Assert.True(File.Exists(Path.Combine(root, "file' [one].txt")));
        }
        finally
        {
            // Removing this unique drive is safe even if an assertion failed before the normal removal.
            if (createdDrives is not null)
            {
                var row = createdDrives.Rows.FirstOrDefault(row => row.Cells["Name"].Display == name);
                if (row is not null)
                    await session.ExecuteAsync(ConsoleActionId.RemoveDrive, createdDrives.Id, [row.Handle], new Dictionary<string, string>());
            }
            Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true);
        }
    }

    [Fact]
    public async Task MaintainsEnvironmentAndBindsValuesWithoutScriptInjection()
    {
        await using var session = new PowerShellSession();
        var key = "RUNSPACE_TEST_" + Guid.NewGuid().ToString("N");
        var secondKey = key + "_SECOND";
        Environment.SetEnvironmentVariable(key, "before");
        Environment.SetEnvironmentVariable(secondKey, "before");
        try
        {
            var result = await session.QueryAsync(Node(ResourceKind.Environment));
            AssertCompleted(result);
            var row = Assert.Single(result.Rows, row => row.Cells["Name"].Display == key);
            var secondRow = Assert.Single(result.Rows, row => row.Cells["Name"].Display == secondKey);
            const string literal = "'; throw 'injected'; $(Get-Process) [literal] ` \n second line";
            var edited = await session.ExecuteAsync(ConsoleActionId.SetValue, result.Id, [row.Handle],
                new Dictionary<string, string> { ["Value"] = literal });
            AssertCompleted(edited);
            Assert.Contains("-LiteralPath " + PowerShellDisplay.Literal($"Env:\\{key}"), edited.Script);
            Assert.Contains("-Value " + PowerShellDisplay.Literal(literal), edited.Script);
            AssertCompleted(await session.ExecuteAsync(ConsoleActionId.SetValue, result.Id, [secondRow.Handle],
                new Dictionary<string, string> { ["Value"] = literal }));
            var refreshed = await session.QueryAsync(Node(ResourceKind.Environment));
            AssertCompleted(refreshed);
            Assert.Equal(literal, Assert.Single(refreshed.Rows, item => item.Cells["Name"].Display == key).Cells["Value"].Value);
            Assert.Equal(literal, Assert.Single(refreshed.Rows, item => item.Cells["Name"].Display == secondKey).Cells["Value"].Value);
            AssertCompleted(await session.ExecuteAsync(ConsoleActionId.RemoveItem, result.Id, [row.Handle, secondRow.Handle], new Dictionary<string, string>()));
            var removed = await session.QueryAsync(Node(ResourceKind.Environment));
            Assert.DoesNotContain(removed.Rows, item => item.Cells["Name"].Display == key);
            Assert.DoesNotContain(removed.Rows, item => item.Cells["Name"].Display == secondKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable(secondKey, null);
        }
    }

    [Fact]
    public async Task RejectsReleasedUnknownAndMismatchedHandlesBeforeMutation()
    {
        await using var session = new PowerShellSession();
        var result = await session.QueryAsync(Node(ResourceKind.Environment));
        AssertCompleted(result);
        var unknown = await session.ExecuteAsync(ConsoleActionId.RemoveItem, result.Id, [Guid.NewGuid()], new Dictionary<string, string>());
        Assert.Equal(InvocationOutcome.Failed, unknown.Outcome);
        Assert.Contains(unknown.Diagnostics, diagnostic => diagnostic.Message.Contains("stale"));
        var row = Assert.Single(result.Rows.Take(1));
        var mismatch = await session.ExecuteAsync(ConsoleActionId.StopProcess, result.Id, [row.Handle], new Dictionary<string, string>());
        Assert.Equal(InvocationOutcome.Failed, mismatch.Outcome);
        session.ReleaseResult(result.Id);
        var stale = await session.ExecuteAsync(ConsoleActionId.RemoveItem, result.Id, [row.Handle], new Dictionary<string, string>());
        Assert.Equal(InvocationOutcome.Failed, stale.Outcome);
        Assert.Contains(stale.Diagnostics, diagnostic => diagnostic.Message.Contains("released"));
    }

    [Fact]
    public async Task CapturesAllDiagnosticStreamsAndNonterminatingErrors()
    {
        await using var session = new PowerShellSession();
        var result = await session.InvokeForTestingAsync("""
            $VerbosePreference = 'Continue'
            $DebugPreference = 'Continue'
            Write-Output before
            Write-Warning warning-test
            Write-Information info-test -InformationAction Continue
            Write-Verbose verbose-test
            Write-Debug debug-test
            Write-Progress -Activity progress-test -Status running -PercentComplete 50
            Write-Error error-test
            Write-Output after
            """);
        Assert.Equal(InvocationOutcome.CompletedWithErrors, result.Outcome);
        Assert.Equal(["before", "after"], result.Rows.Select(row => row.Cells["Value"].Display));
        foreach (var stream in new[] { "Error", "Warning", "Information", "Verbose", "Debug", "Progress" })
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stream == stream);
    }

    [Fact]
    public async Task RetainsOutputOnTerminatingErrorsAndReportsPropertyFailures()
    {
        await using var session = new PowerShellSession();
        var failed = await session.InvokeForTestingAsync("'before'; throw 'terminated-test'; 'never'");
        Assert.Equal(InvocationOutcome.Failed, failed.Outcome);
        Assert.Equal("before", Assert.Single(failed.Rows).Cells["Value"].Display);
        Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Message.Contains("terminated-test"));
        var live = await session.InvokeForTestingAsync("""
            $object = [pscustomobject]@{ Value = 'live' }
            $object | Add-Member -MemberType ScriptProperty -Name Broken -Value { throw 'getter-test' } -PassThru
            """);
        AssertCompleted(live);
        var inspected = await session.InspectAsync(live.Id, Assert.Single(live.Rows).Handle);
        Assert.Contains(inspected, property => property.Name == "Broken" && property.Error != null && property.Error.Contains("getter-test"));
        Assert.Contains(inspected, property => property.Name == "Value" && property.Value == "live");
        AssertCompleted(await session.QueryAsync(Node(ResourceKind.Providers)));
    }

    [Fact]
    public async Task CooperativelyStopsAndRetainsPartialOutputThenReusesRunspace()
    {
        await using var session = new PowerShellSession();
        AssertCompleted(await session.QueryAsync(Node(ResourceKind.Providers)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var watch = Stopwatch.StartNew();
        var result = await session.InvokeForTestingAsync("'started'; Start-Sleep -Seconds 30; 'never'", cancellation.Token);
        Assert.Equal(InvocationOutcome.Cancelled, result.Outcome);
        Assert.Equal("started", Assert.Single(result.Rows).Cells["Value"].Display);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
        AssertCompleted(await session.QueryAsync(Node(ResourceKind.Providers)));
    }

    [Fact]
    public async Task SerializesQueuedOperationsAndSupportsQueuedCancellation()
    {
        await using var session = new PowerShellSession();
        AssertCompleted(await session.QueryAsync(Node(ResourceKind.Providers)));
        var first = session.InvokeForTestingAsync("'first'; Start-Sleep -Milliseconds 300; 'last'");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await session.QueryAsync(Node(ResourceKind.Environment), cancellation.Token);
        Assert.Equal(InvocationOutcome.Cancelled, cancelled.Outcome);
        AssertCompleted(await first);
        AssertCompleted(await session.QueryAsync(Node(ResourceKind.Environment)));
    }

    [Fact]
    public async Task NetworkViewsAreRealAndWindowsViewsAreExplicitlyUnavailableElsewhere()
    {
        await using var session = new PowerShellSession();
        var properties = await session.QueryAsync(Node(ResourceKind.NetworkProperties));
        string[] expectedColumns = OperatingSystem.IsWindows()
            ? ["HostName", "DomainName", "DhcpScopeName", "IsWinsProxy", "NodeType"]
            : ["HostName", "DomainName", "NodeType"];
        Assert.Equal(expectedColumns, properties.Columns.Select(column => column.Key));
        AssertCompleted(properties);
        var row = Assert.Single(properties.Rows);
        Assert.All(row.Cells.Values, cell => Assert.Null(cell.Error));
        var nativeProperties = IPGlobalProperties.GetIPGlobalProperties();
        Assert.Equal(nativeProperties.HostName, row.Cells["HostName"].Value);
        Assert.Equal(nativeProperties.DomainName, row.Cells["DomainName"].Value);
        AssertCompleted(await session.QueryAsync(Node(ResourceKind.NetworkInterfaces)));
        if (!OperatingSystem.IsWindows())
        {
            foreach (var node in BuiltInCatalog.LocalSystem.Where(node => node.WindowsOnly))
            {
                var result = await session.QueryAsync(node);
                Assert.Equal(InvocationOutcome.Failed, result.Outcome);
                Assert.Empty(result.Rows);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stream == "Error" && diagnostic.Message.Contains("Windows"));
            }
        }
    }

    [Fact]
    public async Task ReadsWindowsSystemViewsAndTheirRelatedNavigation()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var session = new PowerShellSession();
        foreach (var kind in new[] { ResourceKind.Services, ResourceKind.Shares, ResourceKind.LocalUsers, ResourceKind.LocalGroups })
        {
            var result = await session.QueryAsync(Node(kind));
            AssertRead(result);
            if (kind != ResourceKind.Shares) Assert.NotEmpty(result.Rows);
            if (kind == ResourceKind.LocalGroups)
            {
                var group = Assert.Single(result.Rows.Take(1));
                AssertRead(await session.QueryAsync(group.RelatedNode!));
            }
        }
        var logs = await session.QueryAsync(Node(ResourceKind.EventLogs));
        AssertRead(logs);
        var populatedLog = logs.Rows.FirstOrDefault(row => Convert.ToInt64(row.Cells["RecordCount"].Value) > 0);
        if (populatedLog is not null)
        {
            var entries = await session.QueryAsync(populatedLog.RelatedNode!);
            AssertRead(entries);
            Assert.InRange(entries.Rows.Count, 1, 200);
        }
        var registry = await session.QueryAsync(Node(ResourceKind.Registry));
        AssertCompleted(registry);
        Assert.Equal(2, registry.Rows.Count);
        var keys = await session.QueryAsync(registry.Rows[1].RelatedNode!);
        AssertRead(keys);
        Assert.NotEmpty(keys.Rows);
        Assert.NotEmpty(await session.InspectAsync(keys.Id, keys.Rows[0].Handle));
        var namespaces = await session.QueryAsync(Node(ResourceKind.WmiNamespaces));
        AssertRead(namespaces);
        Assert.NotEmpty(namespaces.Rows);
        var classesNode = Assert.Single(namespaces.Rows, row => row.RelatedNode?.Kind == ResourceKind.WmiClasses).RelatedNode!;
        var classes = await session.QueryAsync(classesNode);
        AssertRead(classes);
        var namespaceClass = Assert.Single(classes.Rows, row => row.Cells["CimClassName"].Display.Equals("__Namespace", StringComparison.OrdinalIgnoreCase));
        AssertRead(await session.QueryAsync(namespaceClass.RelatedNode!));
    }

    [Fact]
    public async Task ReadsLocalAccountsThroughAdsiWithoutAnInstalledPowerShellModule()
    {
        if (!OperatingSystem.IsWindows()) return;
        var originalPath = Environment.GetEnvironmentVariable("PSModulePath");
        await using var session = new PowerShellSession();
        try
        {
            AssertCompleted(await session.InvokeForTestingAsync("""
                $env:PSModulePath = [IO.Path]::Combine([AppContext]::BaseDirectory, 'runtimes', 'win', 'lib', 'net10.0', 'Modules')
                """));
            var users = await session.QueryAsync(Node(ResourceKind.LocalUsers));
            AssertCompleted(users);
            Assert.NotEmpty(users.Rows);
            Assert.Contains(users.Diagnostics, diagnostic => diagnostic.Stream == "Information" && diagnostic.Message.Contains("ADSI"));
            Assert.All(users.Rows, row => Assert.NotEqual("(null)", row.Cells["SID"].Display));
            var groups = await session.QueryAsync(Node(ResourceKind.LocalGroups));
            AssertCompleted(groups);
            Assert.NotEmpty(groups.Rows);
            Assert.Contains(groups.Diagnostics, diagnostic => diagnostic.Message.Contains("ADSI"));
            var administrators = Assert.Single(groups.Rows, row => row.Cells["SID"].Display == "S-1-5-32-544");
            var members = await session.QueryAsync(administrators.RelatedNode!);
            AssertRead(members);
            Assert.NotEmpty(members.Rows);
            Assert.Contains(members.Diagnostics, diagnostic => diagnostic.Message.Contains("ADSI"));
            Assert.NotEmpty(await session.InspectAsync(members.Id, members.Rows[0].Handle));
        }
        finally { Environment.SetEnvironmentVariable("PSModulePath", originalPath); }
    }

    private static void AssertRead(ConsoleResult result) =>
        Assert.True(result.Outcome is InvocationOutcome.Completed or InvocationOutcome.CompletedWithErrors,
            $"{result.Script}: {result.Outcome}{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics.Select(record => $"{record.Stream}: {record.Message}"))}");

    private static void AssertCompleted(ConsoleResult result) =>
        Assert.True(result.Outcome == InvocationOutcome.Completed,
            $"{result.Script}: {result.Outcome}{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics.Select(record => $"{record.Stream}: {record.Message}"))}");
}
