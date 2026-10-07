using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Runspace.Core;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;
using Runspace.PowerShell;

namespace Runspace.Desktop.Tests;

public sealed partial class ConsoleTests
{
    [AvaloniaFact]
    public async Task CachedTwentyThousandRowsKeepSelectionAndScrollingResponsive()
    {
        const int samples = 200;
        var baselineMemory = GC.GetTotalMemory(true);
        var result = ResultBenchmark.CreateFixture();
        var columns = result.Columns;
        var rows = result.Rows;
        var session = new FixtureSession { Query = _ => Task.FromResult(result) };
        var window = new MainWindow(session) { Width = 1200, Height = 800 };
        var ready = Stopwatch.StartNew();
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.UpdateLayout();
            var firstVisible = ready.Elapsed.TotalMilliseconds;
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            var source = grid.ItemsSource;
            var gridColumns = grid.Columns.ToArray();
            var selection = new List<double>();
            var scrolling = new List<double>();
            var realized = 0;
            for (var index = -20; index < samples; index++)
            {
                var row = rows[((index + 20) * 7919) % rows.Count];
                var timer = Stopwatch.StartNew();
                grid.SelectedItem = row;
                window.UpdateLayout();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (index >= 0) selection.Add(timer.Elapsed.TotalMilliseconds);

                timer.Restart();
                grid.ScrollIntoView(row, grid.Columns[0]);
                window.UpdateLayout();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (index >= 0) scrolling.Add(timer.Elapsed.TotalMilliseconds);
                var visible = grid.GetVisualDescendants().OfType<DataGridRow>().ToArray();
                Assert.Contains(visible, item => ReferenceEquals(item.DataContext, row));
                realized = Math.Max(realized, visible.Length);
            }
            Assert.Same(source, grid.ItemsSource);
            Assert.Equal(gridColumns, grid.Columns);
            Assert.Equal(1, session.Queries);
            Assert.Equal(1, window.ResultDisplayCount);
            Assert.Null(session.LastSelection);
            Assert.InRange(realized, 1, 60);
            var retainedBytes = GC.GetTotalMemory(true) - baselineMemory;
            var selectionP95 = Percentile95(selection);
            var scrollP95 = Percentile95(scrolling);
            if (Environment.GetEnvironmentVariable("RUNSPACE_BENCHMARK_DIR") is { Length: > 0 } directory)
            {
#if DEBUG
                Assert.Fail("Responsiveness measurements require a Release build.");
#endif
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "desktop.json"), JsonSerializer.Serialize(new
                {
                    WorkloadRows = rows.Count, ScalarColumns = columns.Count, Samples = samples, WarmupSamples = 20,
                    OS = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
                    LogicalProcessors = Environment.ProcessorCount, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Avalonia = typeof(Window).Assembly.GetName().Version?.ToString(),
                    DataGrid = typeof(DataGrid).Assembly.GetName().Version?.ToString(),
                    ClientWidth = window.ClientSize.Width, ClientHeight = window.ClientSize.Height,
                    Scale = window.RenderScaling, Dpi = 96 * window.RenderScaling,
                    SelectionP95Ms = selectionP95, ScrollP95Ms = scrollP95,
                    FirstVisibleMs = firstVisible, RetainedManagedBytes = retainedBytes, MaxRealizedRows = realized,
                    GridReplacements = window.ResultDisplayCount,
                    Method = "Headless Skia; selection/action-pane + layout + dispatcher barrier; ScrollIntoView + layout + barrier. No physical-display/GPU frame measurement."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Assert.True(selectionP95 <= 100, $"Selection/action-pane p95 was {selectionP95:N2} ms (target 100 ms).");
                Assert.True(scrollP95 <= 100, $"Scroll/layout p95 was {scrollP95:N2} ms (regression budget 100 ms).");
                Assert.True(retainedBytes <= 128 * 1024 * 1024, $"Scalar fixture retained {retainedBytes:N0} bytes (budget 128 MiB).");
            }
        }
        finally { window.Close(); }
    }

    private static double Percentile95(List<double> values) =>
        values.Order().ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1);

    [AvaloniaFact]
    public async Task RealStreamFloodKeepsPreviousGridResponsiveAndPublishesOnlyOneTerminalReplacement()
    {
        await using var engine = new PowerShellSession();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cached = ResultBenchmark.CreateFixture();
        Task<ConsoleResult>? running = null;
        var session = new FixtureSession
        {
            Query = node => node.Kind == ResourceKind.Processes ? Task.FromResult(cached) :
                running = engine.InvokeForTestingAsync("""
                    param($started, $release)
                    $started.Set()
                    for ($i = 1; $i -le 20000; $i++) {
                        [pscustomobject]@{ C0=$i; C1=$i; C2=$i; C3=$i; C4=$i; C5=$i; C6=$i; C7=$i; C8=$i; C9=$i }
                        Write-Warning "fixture-$i"
                        Write-Progress -Activity Fixture -Status "step-$i" -PercentComplete ($i % 100)
                    }
                    if (!$release.Wait(15000)) { throw 'Fixture release timed out' }
                    """, arguments: [started, release], columns: cached.Columns)
        };
        var window = new MainWindow(session);
        TestNavigation.ShowResource(window);
        try
        {
            var model = (ConsoleViewModel)window.DataContext!;
            await UntilAsync(() => !model.IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            var source = grid.ItemsSource;
            var columns = grid.Columns.ToArray();
            window.FindControl<TreeView>("NavigationTree")!.SelectedItem =
                model.Roots[0].Children.Single(item => item.Node.Kind == ResourceKind.Environment);
            Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(10))));
            for (var index = 0; index < 20; index++)
            {
                var row = cached.Rows[index * 997];
                grid.SelectedItem = row;
                grid.ScrollIntoView(row, grid.Columns[0]);
                window.UpdateLayout();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.Contains(grid.GetVisualDescendants().OfType<DataGridRow>(), item => ReferenceEquals(item.DataContext, row));
                Assert.Same(source, grid.ItemsSource);
                Assert.Equal(columns, grid.Columns);
                Assert.Equal(1, window.ResultDisplayCount);
                Assert.True(model.IsBusy);
            }
            release.Set();
            var streamed = await running!.WaitAsync(TimeSpan.FromSeconds(20));
            await UntilAsync(() => !model.IsBusy);
            Assert.Equal(InvocationOutcome.Completed, streamed.Outcome);
            Assert.Equal(20_000, streamed.Rows.Count);
            Assert.Equal(1, streamed.Measurements!.PeakOutputBuffer);
            Assert.Equal(2, window.ResultDisplayCount);
            Assert.Contains("coalesced", model.Diagnostics);
            Assert.Contains(cached.Id, session.Released);
            engine.ReleaseResult(streamed.Id);
        }
        finally
        {
            release.Set();
            window.Close();
            if (running is not null) await running.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }
}
