using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Runspace.Core;
using Runspace.Desktop.ViewModels;
using Runspace.PowerShell;

namespace Runspace.Desktop;

internal sealed class PublishValidation(string reportPath)
{
    private readonly List<PublishCheck> checks = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool RuntimePassed { get; private set; }
    public int ExitCode { get; private set; } = 1;

    public async Task CheckRuntimeAsync()
    {
        var probe = new PublishedRuntimeProbe();
        try
        {
            await probe.RunAsync();
            RuntimePassed = true;
        }
        catch (Exception exception)
        {
            checks.Add(new("Embedded runtime", "Run published runtime probes", false, exception.ToString()));
        }
        finally
        {
            checks.InsertRange(0, probe.Checks);
            WriteReport();
        }
    }

    public void Attach(MainWindow window)
    {
        window.Opened += async (_, _) =>
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
                {
                    var model = (ConsoleViewModel)window.DataContext!;
                    if (window.IsSessionReady && window.FindControl<TreeView>("NavigationTree")!.SelectedItem is null)
                        window.FindControl<TreeView>("NavigationTree")!.SelectedItem =
                            model.Roots[0].Children.Single(item => item.Node.Kind == ResourceKind.Processes);
                    var rows = window.FindControl<DataGrid>("ResultsGrid")!.ItemsSource?.Cast<ConsoleRow>();
                    if (!model.IsBusy && rows?.Any(row => Convert.ToInt32(row.Cells["Id"].Value) == Environment.ProcessId) == true)
                    {
                        if (model.Runtime != $"Local / PowerShell {System.Management.Automation.PSVersionInfo.PSVersion}")
                            throw new InvalidOperationException("The desktop did not display the actual embedded runtime version.");
                        window.UpdateLayout();
                        using var bitmap = new RenderTargetBitmap(new PixelSize(640, 480));
                        bitmap.Render(window);
                        checks.Add(new("Desktop", "Open the real console, display processes/runtime and render the window", true, model.Runtime));
                        ExitCode = 0;
                        break;
                    }
                    await Task.Delay(100);
                }
                if (ExitCode != 0)
                    throw new TimeoutException("Desktop startup did not display the benign process table within 30 seconds. " +
                        ((ConsoleViewModel)window.DataContext!).Diagnostics);
                WriteReport();
            }
            catch (Exception exception) { FailDesktop(exception); }
            window.Close();
        };
    }

    public void FailDesktop(Exception exception)
    {
        ExitCode = 1;
        checks.Add(new("Desktop/native bootstrap", "Initialize Avalonia's actual desktop backend", false,
            exception + (OperatingSystem.IsLinux()
                ? "\nLinux needs an X11/XWayland display and the native libraries listed in docs/usage.md."
                : "\nExtract the entire payload for this OS/architecture and launch in a graphical session.")));
        WriteReport();
    }

    private void WriteReport()
    {
        var report = new
        {
            SchemaVersion = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
            Passed = ExitCode == 0,
            OS = RuntimeInformation.OSDescription,
            OSVersion = Environment.OSVersion.Version.ToString(),
            Distribution = OperatingSystem.IsLinux() && File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : null,
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            DotNet = RuntimeInformation.FrameworkDescription,
            PowerShell = System.Management.Automation.PSVersionInfo.PSVersion.ToString(),
            Avalonia = typeof(Application).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            DataGrid = typeof(DataGrid).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            PayloadDirectory = AppContext.BaseDirectory,
            WorkingDirectory = Environment.CurrentDirectory,
            Checks = checks
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
    }
}
