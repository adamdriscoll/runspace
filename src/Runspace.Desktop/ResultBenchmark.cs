using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Runspace.Core;
using Runspace.Desktop.ViewModels;

namespace Runspace.Desktop;

internal sealed class ResultBenchmark(string reportPath)
{
    private readonly long baselineMemory = GC.GetTotalMemory(true);
    private readonly Stopwatch ready = Stopwatch.StartNew();
    public IConsoleSession Session { get; } = new CachedSession();
    public int ExitCode { get; private set; } = 1;

    public void Attach(MainWindow window)
    {
        window.Opened += async (_, _) =>
        {
            try
            {
                while (((ConsoleViewModel)window.DataContext!).IsBusy)
                {
                    if (ready.Elapsed > TimeSpan.FromSeconds(30))
                        throw new TimeoutException("Cached benchmark window did not become ready.");
                    await Task.Delay(10);
                }
                var model = (ConsoleViewModel)window.DataContext!;
                window.FindControl<TreeView>("NavigationTree")!.SelectedItem =
                    model.Roots[0].Children.Single(item => item.Node.Kind == ResourceKind.Processes);
                window.UpdateLayout();
                var firstVisibleMs = ready.Elapsed.TotalMilliseconds;
                var grid = window.FindControl<DataGrid>("ResultsGrid")!;
                var rows = grid.ItemsSource!.Cast<ConsoleRow>().ToArray();
                if (rows.Length != 20_000 || grid.Columns.Count != 10)
                    throw new InvalidOperationException("Benchmark requires 20,000 rows and ten scalar columns.");
                var source = grid.ItemsSource;
                var columns = grid.Columns.ToArray();
                var selections = new List<double>();
                var scrolls = new List<double>();
                var realized = 0;
                for (var index = -20; index < 200; index++)
                {
                    var row = rows[((index + 20) * 7919) % rows.Length];
                    var timer = Stopwatch.StartNew();
                    grid.SelectedItem = row;
                    window.UpdateLayout();
                    await NextFrameAsync(window);
                    if (index >= 0) selections.Add(timer.Elapsed.TotalMilliseconds);
                    timer.Restart();
                    grid.ScrollIntoView(row, grid.Columns[0]);
                    window.UpdateLayout();
                    await NextFrameAsync(window);
                    if (index >= 0) scrolls.Add(timer.Elapsed.TotalMilliseconds);
                    var visible = grid.GetVisualDescendants().OfType<DataGridRow>().ToArray();
                    realized = Math.Max(realized, visible.Length);
                    if (!visible.Any(item => ReferenceEquals(item.DataContext, row)))
                        throw new InvalidOperationException("Scrolling did not realize the requested row.");
                }
                var selectionP95 = P95(selections);
                var scrollP95 = P95(scrolls);
                var retainedBytes = GC.GetTotalMemory(true) - baselineMemory;
                var passed = selectionP95 <= 100 && scrollP95 <= 100 && realized <= 60 && retainedBytes <= 128 * 1024 * 1024 &&
                    ReferenceEquals(source, grid.ItemsSource) && columns.SequenceEqual(grid.Columns) &&
                    window.ResultDisplayCount == 1;
                Write(new
                {
                    Passed = passed, TimestampUtc = DateTimeOffset.UtcNow,
                    OS = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
                    Avalonia = typeof(Application).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    DataGrid = typeof(DataGrid).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    ClientWidth = window.ClientSize.Width, ClientHeight = window.ClientSize.Height,
                    Scale = window.RenderScaling, Dpi = 96 * window.RenderScaling,
                    WorkloadRows = rows.Length, ScalarColumns = columns.Length, Samples = 200, WarmupSamples = 20,
                    SelectionP95Ms = selectionP95, ScrollP95Ms = scrollP95, FirstVisibleMs = firstVisibleMs,
                    RetainedManagedBytes = retainedBytes,
                    MaxRealizedRows = realized, GridReplacements = window.ResultDisplayCount,
                    Method = "Native Avalonia backend; cached selection/action-pane or ScrollIntoView + layout + next animation-frame callback + background dispatcher barrier. Not a physical scan-out/input-to-photon measurement."
                });
                ExitCode = passed ? 0 : 1;
            }
            catch (Exception exception) { Fail(exception); }
            window.Close();
        };
    }

    public void Fail(Exception exception)
    {
        ExitCode = 1;
        Write(new { Passed = false, Error = exception.ToString(), OS = RuntimeInformation.OSDescription });
    }

    private void Write(object report) =>
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

    private static async Task NextFrameAsync(Window window)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => frame.TrySetResult());
        await frame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static double P95(List<double> values) =>
        values.Order().ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1);

    private sealed class CachedSession : IConsoleSession
    {
        private readonly ConsoleResult result = CreateFixture();
        public string RuntimeVersion => "cached benchmark fixture";
        public Task<ConsoleResult> QueryAsync(ConsoleNode node, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
        public Task<IReadOnlyList<ConsoleNode>> GetDriveNodesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConsoleNode>>([]);
        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderInfo>>([]);
        public Task<ConsoleResult> ExecuteAsync(ConsoleActionId action, Guid resultId, IReadOnlyList<Guid> selection,
            IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The benchmark never executes actions.");
        public Task<IReadOnlyList<ObjectProperty>> InspectAsync(Guid resultId, Guid handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The benchmark uses only already evaluated scalar cells.");
        public void ReleaseResult(Guid resultId) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static ConsoleResult CreateFixture()
    {
        var columns = Enumerable.Range(0, 10).Select(index =>
            new ConsoleColumn($"C{index}", $"Scalar {index}", ColumnKind.Number, 100)).ToArray();
        var rows = Enumerable.Range(1, 20_000).Select(index => new ConsoleRow(
            new Guid(index, 0, 0, new byte[8]),
            columns.ToDictionary(column => column.Key, column => ConsoleCell.From(index, ColumnKind.Number)))).ToArray();
        return new(Guid.NewGuid(), columns, rows, "# Cached scalar fixture", [], TimeSpan.Zero, InvocationOutcome.Completed);
    }
}
