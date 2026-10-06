using System.Runtime.CompilerServices;
using Runspace.Core;
using Runspace.PowerShell;

namespace Runspace.Tests;

public sealed class SessionLifetimeTests
{
    private static readonly ConsoleNode EnvironmentNode =
        BuiltInCatalog.LocalSystem.Single(node => node.Kind == ResourceKind.Environment);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("", InvocationOutcome.Completed, 0)]
    [InlineData("$null", InvocationOutcome.Completed, 0)]
    [InlineData("'usable'; Write-Error 'partial-error'", InvocationOutcome.CompletedWithErrors, 1)]
    [InlineData("'usable'; throw 'terminal-error'", InvocationOutcome.Failed, 1)]
    public async Task EmptyAndPartialInvocationsKeepTheirOutcomeAndUsableHandles(string script, InvocationOutcome outcome, int count)
    {
        await using var session = new PowerShellSession();
        var result = await session.InvokeForTestingAsync(script).WaitAsync(Timeout);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(count, result.Rows.Count);
        if (count == 0) Assert.DoesNotContain(result.Diagnostics, record => record.Stream == "Error");
        else
        {
            var row = Assert.Single(result.Rows);
            Assert.Equal("usable", row.Cells["Value"].Value);
            Assert.NotEmpty(await session.InspectAsync(result.Id, row.Handle));
            Assert.Contains(result.Diagnostics, record => record.Stream == "Error");
        }
        session.ReleaseResult(result.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseOrShutdownDisposesDuplicateObjectsOnceAndDropsTheirGraphEvenWithCachedRows(bool shutdown)
    {
        await using var session = new PowerShellSession();
        var factory = new GraphFactory();
        var result = await session.InvokeForTestingAsync("param($factory) $factory.CreatePair()", arguments: [factory]);
        Assert.Equal(InvocationOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal("cached scalar", row.Cells["Value"].Value));
        Assert.True(factory.Object!.IsAlive);
        Assert.True(factory.Child!.IsAlive);
        if (shutdown) await session.DisposeAsync();
        else
        {
            session.ReleaseResult(result.Id);
            session.ReleaseResult(result.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.InspectAsync(result.Id, result.Rows[0].Handle));
        }
        Assert.Equal(1, factory.Disposals);
        Assert.True(await CollectedAsync(factory.Object, factory.Child), "Released result still retains its live object graph.");
        Assert.All(result.Rows, row => Assert.Equal("cached scalar", row.Cells["Value"].Display));
        GC.KeepAlive(result);
    }

    [Fact]
    public async Task ReleasedResultIsUnavailableImmediatelyButActiveInspectionKeepsItsLeaseUntilGetterReturns()
    {
        await using var session = new PowerShellSession();
        using var gate = new GetterGate();
        var probe = new InspectionProbe(gate);
        var result = await session.InvokeForTestingAsync("param($object) $object", arguments: [probe]);
        var inspection = session.InspectAsync(result.Id, Assert.Single(result.Rows).Handle);
        try
        {
            await gate.Entered.Task.WaitAsync(Timeout);
            session.ReleaseResult(result.Id);
            Assert.Equal(0, gate.Disposals);
            gate.Release.Set();
            var properties = await inspection.WaitAsync(Timeout);
            Assert.Contains(properties, property => property.Name == "Blocked" && property.Value == "getter completed" && property.Error is null);
            Assert.Equal(1, gate.Disposals);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.InspectAsync(result.Id, result.Rows[0].Handle));
        }
        finally { gate.Release.Set(); await inspection.WaitAsync(Timeout); }
    }

    [Fact]
    public async Task InspectionActionAndNavigationShareQueueAndCancellationWhileQueuedDoesNotExecute()
    {
        await using var session = new PowerShellSession();
        using var gate = new GetterGate();
        var key = "RUNSPACE_QUEUE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key, "before");
        try
        {
            var environment = await session.QueryAsync(EnvironmentNode);
            var selected = Assert.Single(environment.Rows, row => row.Cells["Name"].Display == key);
            var objects = await session.InvokeForTestingAsync("param($object) $object", arguments: [new InspectionProbe(gate)]);
            var inspection = session.InspectAsync(objects.Id, Assert.Single(objects.Rows).Handle);
            try
            {
                await gate.Entered.Task.WaitAsync(Timeout);
                var action = session.ExecuteAsync(ConsoleActionId.SetValue, environment.Id, [selected.Handle],
                    new Dictionary<string, string> { ["Value"] = "after" });
                var navigation = session.QueryAsync(EnvironmentNode);
                using var cancellation = new CancellationTokenSource();
                var cancelled = session.ExecuteAsync(ConsoleActionId.SetValue, environment.Id, [selected.Handle],
                    new Dictionary<string, string> { ["Value"] = "must-not-execute" }, cancellation.Token);
                var cancelledInspection = session.InspectAsync(objects.Id, objects.Rows[0].Handle, cancellation.Token);
                cancellation.Cancel();
                Assert.Equal(InvocationOutcome.Cancelled, (await cancelled.WaitAsync(Timeout)).Outcome);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledInspection.WaitAsync(Timeout));
                Assert.False(action.IsCompleted);
                Assert.False(navigation.IsCompleted);
                Assert.Equal("before", Environment.GetEnvironmentVariable(key));
                gate.Release.Set();
                await inspection.WaitAsync(Timeout);
                Assert.Equal(InvocationOutcome.Completed, (await action.WaitAsync(Timeout)).Outcome);
                var navigated = await navigation.WaitAsync(Timeout);
                Assert.Equal(InvocationOutcome.Completed, navigated.Outcome);
                Assert.Equal("after", Assert.Single(navigated.Rows, row => row.Cells["Name"].Display == key).Cells["Value"].Value);
                Assert.Equal("after", Environment.GetEnvironmentVariable(key));
            }
            finally { gate.Release.Set(); await inspection.WaitAsync(Timeout); }
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public async Task DriveCreationRemovalAndFailuresStayOrderedInTheirOwningSession()
    {
        await using var session = new PowerShellSession();
        await using var foreignSession = new PowerShellSession();
        using var creationGate = new GetterGate();
        using var removalGate = new GetterGate();
        var name = "RunspaceQueue" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        var file = Path.Combine(root, "retained.txt");
        var driveNode = BuiltInCatalog.LocalSystem.Single(node => node.Kind == ResourceKind.Drives);
        var parameters = new Dictionary<string, string> { ["Name"] = name, ["Provider"] = "FileSystem", ["Root"] = root };
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(file, "underlying-data");
            var drives = await session.QueryAsync(driveNode);
            Assert.Equal(InvocationOutcome.Completed, drives.Outcome);
            var objects = await session.InvokeForTestingAsync("param($object) $object", arguments: [new InspectionProbe(creationGate)]);
            var inspection = session.InspectAsync(objects.Id, Assert.Single(objects.Rows).Handle);
            try
            {
                await creationGate.Entered.Task.WaitAsync(Timeout);
                var add = session.ExecuteAsync(ConsoleActionId.AddDrive, drives.Id, [], parameters);
                var duplicate = session.ExecuteAsync(ConsoleActionId.AddDrive, drives.Id, [], parameters);
                var query = session.QueryAsync(driveNode);
                Assert.False(add.IsCompleted);
                Assert.False(duplicate.IsCompleted);
                Assert.False(query.IsCompleted);
                Assert.DoesNotContain(await foreignSession.GetDriveNodesAsync().WaitAsync(Timeout), node => node.Path == $"{name}:\\");
                creationGate.Release.Set();
                await inspection.WaitAsync(Timeout);
                Assert.Equal(InvocationOutcome.Completed, (await add.WaitAsync(Timeout)).Outcome);
                var failedAdd = await duplicate.WaitAsync(Timeout);
                Assert.Equal(InvocationOutcome.Failed, failedAdd.Outcome);
                Assert.Contains(failedAdd.Diagnostics, record => record.Stream == "Error" && record.Message.Contains(name));
                drives = await query.WaitAsync(Timeout);
                Assert.Equal(InvocationOutcome.Completed, drives.Outcome);
                Assert.Equal(root, Assert.Single(drives.Rows, row => row.Cells["Name"].Display == name).Cells["Root"].Value);
            }
            finally { creationGate.Release.Set(); await inspection.WaitAsync(Timeout); }

            var selected = Assert.Single(drives.Rows, row => row.Cells["Name"].Display == name);
            var removalObjects = await session.InvokeForTestingAsync("param($object) $object", arguments: [new InspectionProbe(removalGate)]);
            var removalInspection = session.InspectAsync(removalObjects.Id, Assert.Single(removalObjects.Rows).Handle);
            try
            {
                await removalGate.Entered.Task.WaitAsync(Timeout);
                var before = session.GetDriveNodesAsync();
                var remove = session.ExecuteAsync(ConsoleActionId.RemoveDrive, drives.Id, [selected.Handle], new Dictionary<string, string>());
                var missing = session.ExecuteAsync(ConsoleActionId.RemoveDrive, drives.Id, [selected.Handle], new Dictionary<string, string>());
                var after = session.GetDriveNodesAsync();
                Assert.False(before.IsCompleted);
                Assert.False(remove.IsCompleted);
                Assert.False(missing.IsCompleted);
                Assert.False(after.IsCompleted);
                removalGate.Release.Set();
                await removalInspection.WaitAsync(Timeout);
                Assert.Contains(await before.WaitAsync(Timeout), node => node.Path == $"{name}:\\");
                Assert.Equal(InvocationOutcome.Completed, (await remove.WaitAsync(Timeout)).Outcome);
                var failedRemove = await missing.WaitAsync(Timeout);
                Assert.Equal(InvocationOutcome.Failed, failedRemove.Outcome);
                Assert.Contains(failedRemove.Diagnostics, record => record.Stream == "Error" && record.Message.Contains(name));
                Assert.DoesNotContain(await after.WaitAsync(Timeout), node => node.Path == $"{name}:\\");
                Assert.DoesNotContain(await foreignSession.GetDriveNodesAsync(), node => node.Path == $"{name}:\\");
                Assert.Equal("underlying-data", await File.ReadAllTextAsync(file));
            }
            finally { removalGate.Release.Set(); await removalInspection.WaitAsync(Timeout); }
        }
        finally
        {
            creationGate.Release.Set();
            removalGate.Release.Set();
            await session.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ShutdownCancelsQueuedWorkButWaitsForNoncooperativeInspectionBeforeDisposingObjects()
    {
        await using var session = new PowerShellSession();
        using var gate = new GetterGate();
        var result = await session.InvokeForTestingAsync("param($object) $object", arguments: [new InspectionProbe(gate)]);
        var inspection = session.InspectAsync(result.Id, Assert.Single(result.Rows).Handle);
        try
        {
            await gate.Entered.Task.WaitAsync(Timeout);
            var queuedQuery = session.QueryAsync(EnvironmentNode);
            var queuedInspection = session.InspectAsync(result.Id, result.Rows[0].Handle);
            var shutdown = session.DisposeAsync().AsTask();
            Assert.Equal(InvocationOutcome.Cancelled, (await queuedQuery.WaitAsync(Timeout)).Outcome);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedInspection.WaitAsync(Timeout));
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(0, gate.Disposals);
            gate.Release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection.WaitAsync(Timeout));
            await shutdown.WaitAsync(Timeout);
            Assert.Equal(1, gate.Disposals);
        }
        finally
        {
            gate.Release.Set();
            try { await inspection.WaitAsync(Timeout); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task StopDuringDisplayGetterRemainsSerializedAndReportsCancelledAfterGetterReturns()
    {
        await using var session = new PowerShellSession();
        using var gate = new GetterGate();
        using var cancellation = new CancellationTokenSource();
        var running = session.InvokeForTestingAsync("param($object) $object", cancellation.Token, [new DisplayProbe(gate)]);
        try
        {
            await gate.Entered.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            var queued = session.QueryAsync(EnvironmentNode);
            Assert.False(running.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.Equal(0, gate.Disposals);
            gate.Release.Set();
            var result = await running.WaitAsync(Timeout);
            Assert.Equal(InvocationOutcome.Cancelled, result.Outcome);
            Assert.Equal("getter completed", Assert.Single(result.Rows).Cells["Value"].Value);
            Assert.Equal(InvocationOutcome.Completed, (await queued.WaitAsync(Timeout)).Outcome);
            session.ReleaseResult(result.Id);
            Assert.Equal(1, gate.Disposals);
        }
        finally { gate.Release.Set(); await running.WaitAsync(Timeout); }
    }

    private static async Task<bool> CollectedAsync(params WeakReference[] references)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (references.All(reference => !reference.IsAlive)) return true;
            await Task.Delay(20);
        }
        return false;
    }

    public sealed class GraphFactory
    {
        public WeakReference? Object { get; private set; }
        public WeakReference? Child { get; private set; }
        public int Disposals { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public GraphObject[] CreatePair()
        {
            var value = new GraphObject(() => Disposals++);
            Object = new(value);
            Child = new(value.Child);
            return [value, value];
        }
    }

    public sealed class GraphObject(Action disposed) : IDisposable
    {
        public string Value => "cached scalar";
        public object Child { get; } = new();
        public void Dispose() => disposed();
    }

    public sealed class GetterGate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int Disposals;
        public void Dispose() => Release.Dispose();
    }

    public sealed class InspectionProbe : IDisposable
    {
        private readonly GetterGate gate;
        public InspectionProbe(GetterGate gate) => this.gate = gate;
        public string Value => "cached scalar";
        public string Blocked
        {
            get
            {
                gate.Entered.TrySetResult();
                if (!gate.Release.Wait(Timeout)) throw new TimeoutException("Inspection fixture was not released.");
                if (gate.Disposals != 0) throw new ObjectDisposedException(nameof(InspectionProbe));
                return "getter completed";
            }
        }
        public void Dispose() => Interlocked.Increment(ref gate.Disposals);
    }

    public sealed class DisplayProbe(GetterGate gate) : IDisposable
    {
        public string Value
        {
            get
            {
                gate.Entered.TrySetResult();
                if (!gate.Release.Wait(Timeout)) throw new TimeoutException("Display fixture was not released.");
                return "getter completed";
            }
        }
        public void Dispose() => Interlocked.Increment(ref gate.Disposals);
    }
}
