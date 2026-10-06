using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Runspace.Core;
using Runspace.PowerShell;

namespace Runspace.Tests;

public sealed class RetentionTests
{
    [Fact]
    public void StreamTailsBoundFloodingAndKeepErrorsSeparateAndProgressLatest()
    {
        var buffer = new DiagnosticBuffer();
        buffer.Add(new(DateTimeOffset.UnixEpoch, "Error", "preserved-error"));
        for (var index = 0; index < 20_000; index++)
        {
            buffer.Add(new(DateTimeOffset.UnixEpoch, "Warning", $"warning-{index}"));
            buffer.Add(new(DateTimeOffset.UnixEpoch, "Progress", $"progress-{index}"));
        }
        var records = buffer.Snapshot();
        Assert.Contains(records, record => record.Message == "preserved-error");
        Assert.Equal(RetentionPolicy.DiagnosticRecords, records.Count(record => record.Stream == "Warning"));
        Assert.Equal("progress-19999", Assert.Single(records, record => record.Stream == "Progress").Message);
        Assert.Contains(records, record => record.Stream == "Retention" && record.Message.Contains("evicted"));
        Assert.Equal(40_001, buffer.ReceivedCount);
        Assert.InRange(buffer.PeakCount, 1, RetentionPolicy.DiagnosticRecords + RetentionPolicy.ErrorRecords + 1);
        buffer.Add(new(DateTimeOffset.UnixEpoch, "Error", new string('x', RetentionPolicy.MessageCharacters + 1)));
        Assert.Contains(buffer.Snapshot(), record => record.Stream == "Error" && record.Message.Contains("Message shortened"));
    }

    [Fact]
    public async Task OutputLimitStopsExecutionVisiblyAndReleasesRetainedAndOverflowObjects()
    {
        await using var session = new PowerShellSession();
        var factory = new CountedFactory();
        var result = await session.InvokeForTestingAsync("""
            param($factory)
            for ($i = 0; $i -lt 100000; $i++) { $factory.Create() }
            """, arguments: [factory]).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(InvocationOutcome.Failed, result.Outcome);
        Assert.Equal(RetentionPolicy.ResultRows, result.Rows.Count);
        Assert.Contains("25,000", result.RetentionNotice);
        Assert.Contains("Export", result.RetentionNotice);
        Assert.True(result.Measurements!.OutputReceived > RetentionPolicy.ResultRows);
        Assert.InRange(result.Measurements.PeakOutputBuffer, 1, 1);
        Assert.True(factory.Disposals > 0);
        Assert.NotEmpty(await session.InspectAsync(result.Id, result.Rows[0].Handle));
        session.ReleaseResult(result.Id);
        Assert.Equal(factory.Created, factory.Disposals);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InspectAsync(result.Id, result.Rows[0].Handle));
    }

    [Fact]
    public async Task ErrorFloodStopsRatherThanBecomingACompletedOrSilentEmptyResult()
    {
        await using var session = new PowerShellSession();
        var result = await session.InvokeForTestingAsync("""
            'partial'
            for ($i = 0; $i -lt 10000; $i++) { Write-Error "failure-$i" }
            'must-not-complete'
            """).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(InvocationOutcome.Failed, result.Outcome);
        Assert.Equal("partial", Assert.Single(result.Rows).Cells["Value"].Value);
        Assert.Contains("error budget", result.RetentionNotice);
        Assert.InRange(result.Diagnostics.Count(record => record.Stream == "Error"), 1, RetentionPolicy.ErrorRecords);
        Assert.Contains(result.Diagnostics, record => record.Stream == "Error" && record.Message.Contains("failure-"));
        Assert.Contains(result.Diagnostics, record => record.Message == "failure-0");
    }

    [Fact]
    public async Task ResultBudgetReleasesRejectedOutputWithoutInvalidatingExistingHandles()
    {
        await using var session = new PowerShellSession();
        var factory = new CountedFactory();
        var first = await session.InvokeForTestingAsync("param($factory) $factory.Create()", arguments: [factory]);
        for (var index = 0; index < RetentionPolicy.ResultSets + 1; index++)
            Assert.Equal(InvocationOutcome.Completed, (await session.InvokeForTestingAsync("")).Outcome);
        Assert.Equal(0, factory.Disposals);
        for (var index = 1; index < RetentionPolicy.ResultSets; index++)
            await session.InvokeForTestingAsync("'next'");
        var rejected = await session.InvokeForTestingAsync("param($factory) $factory.Create()", arguments: [factory]);
        Assert.Equal(1, factory.Disposals);
        Assert.Equal(InvocationOutcome.Failed, rejected.Outcome);
        Assert.Empty(rejected.Rows);
        Assert.Contains("New output was released", rejected.RetentionNotice);
        Assert.NotEmpty(await session.InspectAsync(first.Id, first.Rows[0].Handle));
        var providerFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetProvidersAsync());
        Assert.Contains("budget is full", providerFailure.Message);
        session.ReleaseResult(first.Id);
        Assert.Equal(2, factory.Disposals);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InspectAsync(first.Id, first.Rows[0].Handle));
    }

    [Fact]
    public async Task ReleaseMeasurementsCoverFirstOutputFloodingMemoryAndCooperativeStop()
    {
        if (Environment.GetEnvironmentVariable("RUNSPACE_BENCHMARK_DIR") is not { Length: > 0 } directory) return;
#if DEBUG
        Assert.Fail("Runtime measurements require a Release build.");
#endif
        await using var session = new PowerShellSession();
        var cold = await session.InvokeForTestingAsync("'warmup'");
        session.ReleaseResult(cold.Id);
        var columns = Enumerable.Range(0, 10).Select(index =>
            new ConsoleColumn($"C{index}", $"Scalar {index}", ColumnKind.Number)).ToArray();
        const string script = """
            for ($i = 1; $i -le 20000; $i++) {
                [pscustomobject]@{ C0=$i; C1=$i; C2=$i; C3=$i; C4=$i; C5=$i; C6=$i; C7=$i; C8=$i; C9=$i }
            }
            """;
        var first = new List<double>();
        var complete = new List<double>();
        var retained = new List<long>();
        var peaks = new List<int>();
        for (var index = -1; index < 10; index++)
        {
            var before = GC.GetTotalMemory(true);
            var result = await session.InvokeForTestingAsync(script, columns: columns).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(InvocationOutcome.Completed, result.Outcome);
            Assert.Equal(20_000, result.Rows.Count);
            Assert.Equal(10, result.Columns.Count);
            Assert.NotNull(result.Measurements!.FirstOutputLatency);
            if (index >= 0)
            {
                first.Add(result.Measurements.FirstOutputLatency.Value.TotalMilliseconds);
                complete.Add(result.Duration.TotalMilliseconds);
                retained.Add(GC.GetTotalMemory(true) - before);
                peaks.Add(result.Measurements.PeakOutputBuffer);
            }
            session.ReleaseResult(result.Id);
        }
        var floodTimer = Stopwatch.StartNew();
        var flood = await session.InvokeForTestingAsync("""
            $VerbosePreference = 'Continue'; $DebugPreference = 'Continue'
            for ($i = 0; $i -lt 20000; $i++) {
                Write-Warning "warning-$i"; Write-Verbose "verbose-$i"; Write-Debug "debug-$i"
                Write-Information "information-$i" -InformationAction Continue
                Write-Progress -Activity Fixture -Status "step-$i" -PercentComplete ($i % 100)
            }
            Write-Error 'preserved-flood-error'; 'usable'
            """).WaitAsync(TimeSpan.FromSeconds(60));
        floodTimer.Stop();
        Assert.Equal(InvocationOutcome.CompletedWithErrors, flood.Outcome);
        Assert.Contains(flood.Diagnostics, record => record.Message == "preserved-flood-error");
        Assert.Single(flood.Diagnostics, record => record.Stream == "Progress");
        Assert.InRange(flood.Measurements!.PeakDiagnosticBuffer, 1,
            RetentionPolicy.DiagnosticRecords + RetentionPolicy.ErrorRecords + 1);
        var stops = new List<double>();
        for (var index = 0; index < 10; index++)
        {
            using var started = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            var running = session.InvokeForTestingAsync(
                "param($started) 'partial'; $started.Set(); Start-Sleep -Seconds 30; 'never'",
                cancellation.Token, [started]);
            Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(10))));
            await Task.Delay(100);
            var timer = Stopwatch.StartNew();
            cancellation.Cancel();
            var stopped = await running.WaitAsync(TimeSpan.FromSeconds(2));
            stops.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Equal(InvocationOutcome.Cancelled, stopped.Outcome);
            Assert.Equal("partial", Assert.Single(stopped.Rows).Cells["Value"].Value);
            session.ReleaseResult(stopped.Id);
        }
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "runtime.json"), JsonSerializer.Serialize(new
        {
            OS = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
            PowerShell = session.RuntimeVersion, LogicalProcessors = Environment.ProcessorCount,
            Samples = 10, WorkloadRows = 20_000, ScalarColumns = 10, ColdEngineWarmupMs = cold.Duration.TotalMilliseconds,
            FirstOutputP95Ms = P95(first), CompletedCachedResultP95Ms = P95(complete),
            RetainedManagedBytesMedian = retained.Order().ElementAt(retained.Count / 2),
            PeakOutputQueueDepth = peaks.Max(), PendingDispatcherObjectNotifications = 0,
            FloodMs = floodTimer.Elapsed.TotalMilliseconds, FloodRecordsReceived = flood.Measurements.DiagnosticsReceived,
            FloodPeakRetainedRecords = flood.Measurements.PeakDiagnosticBuffer, FloodFinalRecords = flood.Diagnostics.Count,
            StopSamples = stops.Count, StopP95Ms = P95(stops), StopMaxMs = stops.Max(), StopTargetMs = 2000,
            Method = "In-process Release engine; first output measured from RunAsync entry; full-GC managed-heap delta includes retained live objects and cached scalar rows; synchronous SDK ReadAll drainage; no per-object dispatcher queue. Warm runspace + one workload warmup."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(stops.Max() <= 2000);
        Assert.True(retained.Order().ElementAt(retained.Count / 2) <= 128 * 1024 * 1024);
    }

    private static double P95(List<double> samples) =>
        samples.Order().ElementAt((int)Math.Ceiling(samples.Count * 0.95) - 1);

    public sealed class CountedFactory
    {
        public int Created;
        public int Disposals;
        public CountedObject Create()
        {
            Interlocked.Increment(ref Created);
            return new(this);
        }
    }

    public sealed class CountedObject(CountedFactory factory) : IDisposable
    {
        public string Value => "cached scalar";
        public void Dispose() => Interlocked.Increment(ref factory.Disposals);
    }
}
