using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Media.Imaging;
using Iseberg.Editor;
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
                    var rows = window.FindControl<DataGrid>("ResultsGrid")!.ItemsSource?.Cast<ConsoleRow>();
                    if (!model.IsBusy && rows?.Any(row => Convert.ToInt32(row.Cells["Id"].Value) == Environment.ProcessId) == true)
                    {
                        if (model.Runtime != $"Local / PowerShell {System.Management.Automation.PSVersionInfo.PSVersion}")
                            throw new InvalidOperationException("The desktop did not display the actual embedded runtime version.");
                        window.UpdateLayout();
                        using var bitmap = new RenderTargetBitmap(new PixelSize(640, 480));
                        bitmap.Render(window);
                        checks.Add(new("Desktop", "Open the real console, display processes/runtime and render the window", true, model.Runtime));
                        CheckEditors(window);
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

    private void CheckEditors(MainWindow window)
    {
        var tabs = window.FindControl<TabControl>("DocumentTabs")!;
        var editor = window.FindControl<PowerShellEditorControl>("ScriptEditor")!;
        var preview = window.FindControl<PowerShellEditorControl>("CurrentScriptEditor")!;
        var grid = window.FindControl<DataGrid>("ResultsGrid")!;
        var items = grid.ItemsSource;
        var model = (ConsoleViewModel)window.DataContext!;
        var history = model.History;
        var previewText = preview.Document.Text;
        var references = typeof(PowerShellEditorControl).Assembly.GetReferencedAssemblies();
        if (references.Any(reference => reference.Name is "System.Management.Automation" or "Iseberg.Core" or "Iseberg"))
            throw new InvalidOperationException("The published editor references a runtime-owning assembly.");
        foreach (var notice in new[] { "ThirdPartyNotices\\Iseberg\\LICENSE", "ThirdPartyNotices\\Iseberg\\editor-notices.md", "ThirdPartyNotices\\AvaloniaEdit.txt" })
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, notice.Replace('\\', Path.DirectorySeparatorChar))))
                throw new FileNotFoundException("Extract the complete payload, including editor license notices.", notice);
        try
        {
            tabs.SelectedItem = window.FindControl<TabItem>("ScriptEditorTab");
            window.UpdateLayout();
            if (!editor.FocusEditor()) throw new InvalidOperationException("The native editing area could not receive focus.");
            const string fixture = "configuration Fixture { Node localhost { } }\n# editing only";
            editor.TextEditor.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = fixture });
            if (editor.Document.Text != fixture || !window.ScriptWorkspace.IsDirty ||
                editor.Analysis.State != EditorAnalysisState.Unavailable || editor.EnableExecutionGestures)
                throw new InvalidOperationException("The native editor did not preserve editing-only/dirty state.");
            editor.TextEditor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F5 });
            editor.TextEditor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F8 });
            editor.ShowFind();
            using (var bitmap = new RenderTargetBitmap(new PixelSize(800, 600))) bitmap.Render(window);
            if (!editor.TextEditor.Undo() || editor.Document.Text.Length != 0 || window.ScriptWorkspace.IsDirty)
                throw new InvalidOperationException("The native editor did not undo its text input.");
            window.RequestedThemeVariant = App.HighContrastTheme;
            if (editor.EnableSyntaxHighlighting || preview.EnableSyntaxHighlighting)
                throw new InvalidOperationException("High contrast retained a lexical palette.");
            tabs.SelectedItem = window.FindControl<TabItem>("CurrentScriptTab");
            window.UpdateLayout();
            preview.FocusEditor();
            preview.TextEditor.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "must not change" });
            var value = ControlAutomationPeer.CreatePeerForElement(preview.TextEditor) as IValueProvider;
            if (preview.Document.Text != previewText || value?.IsReadOnly != true ||
                editor.Document == preview.Document || !preview.IsReadOnly)
                throw new InvalidOperationException("The native script preview was not independently read-only.");
            tabs.SelectedItem = window.FindControl<TabItem>("ResultsTab");
            if (!ReferenceEquals(items, grid.ItemsSource) || model.History != history)
                throw new InvalidOperationException("Editor switching changed the administration results/history.");
            CheckEditorImeLifecycle(window, editor, window.FindControl<TabItem>("ScriptEditorTab")!);
            CheckEditorImeLifecycle(window, preview, window.FindControl<TabItem>("CurrentScriptTab")!);
            CheckEditorImeDisposal(window);
            checks.Add(new("Editor", "Render the published editing-only and read-only controls; input, undo, find, contrast and F5/F8 isolation",
                true, "PoshTools.Iseberg.Editor 0.0.4 / AvaloniaEdit 12.0.0; semantic diagnostics unavailable. Includes real IME client queries during ten focused tab cycles and disposal. Synthetic input only, not physical keyboard or screen-reader certification."));
        }
        finally
        {
            window.ScriptWorkspace.Document.Text = string.Empty;
            window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            tabs.SelectedItem = window.FindControl<TabItem>("ResultsTab");
        }
    }

    private static void CheckEditorImeLifecycle(MainWindow window, PowerShellEditorControl editor, TabItem tab)
    {
        var tabs = window.FindControl<TabControl>("DocumentTabs")!;
        var document = editor.Document;
        var originalText = document.Text;
        TextInputMethodClient? client = null;
        var notifications = 0;
        void QueryClient(object? sender, EventArgs args)
        {
            _ = client!.SurroundingText;
            _ = client.Selection;
            notifications++;
        }
        try
        {
            tabs.SelectedItem = tab;
            window.UpdateLayout();
            document.Text = "# IME lifecycle fixture\n# second line";
            if (!editor.FocusEditor()) throw new InvalidOperationException("The native IME fixture could not focus its editor.");
            editor.CaretOffset = 0;
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.TextEditor.TextArea.RaiseEvent(request);
            client = request.Client;
            if (editor.IsReadOnly ? client is not null : client is null)
                throw new InvalidOperationException("The editor's actual IME client did not match its read-only state.");
            if (client is not null) client.SurroundingTextChanged += QueryClient;
            editor.CaretOffset = 4;
            for (var iteration = 0; iteration < 10; iteration++)
            {
                tabs.SelectedItem = window.FindControl<TabItem>("ResultsTab");
                tabs.SelectedItem = tab;
                window.UpdateLayout();
                if (!editor.FocusEditor() || !ReferenceEquals(document, editor.Document) || editor.CaretOffset != 4)
                    throw new InvalidOperationException("IME detach/reattach lost the host document or caret.");
                editor.CaretOffset = 5;
                editor.CaretOffset = 4;
            }
            if (client is not null && notifications == 0 || document.Text != "# IME lifecycle fixture\n# second line")
                throw new InvalidOperationException("IME lifecycle probes did not query the real client or changed the host document.");
        }
        finally
        {
            if (client is not null) client.SurroundingTextChanged -= QueryClient;
            document.Text = originalText;
        }
    }

    private static void CheckEditorImeDisposal(MainWindow owner)
    {
        var document = new AvaloniaEdit.Document.TextDocument("# IME disposal fixture");
        using var editor = new PowerShellEditorControl(document);
        var probe = new Window { Content = editor, Width = 500, Height = 200, Title = "Editor lifecycle validation" };
        TextInputMethodClient? client = null;
        var notifications = 0;
        void QueryClient(object? sender, EventArgs args)
        {
            _ = client!.SurroundingText;
            _ = client.Selection;
            notifications++;
        }
        try
        {
            probe.Show(owner);
            probe.UpdateLayout();
            if (!editor.FocusEditor()) throw new InvalidOperationException("The native disposal probe could not focus its editor.");
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.TextEditor.TextArea.RaiseEvent(request);
            client = request.Client ?? throw new InvalidOperationException("The disposal probe did not expose its actual IME client.");
            client.SurroundingTextChanged += QueryClient;
            editor.CaretOffset = 4;
            editor.Dispose();
            if (notifications == 0 || document.Text != "# IME disposal fixture")
                throw new InvalidOperationException("IME disposal did not preserve the host document or query the client.");
        }
        finally
        {
            if (client is not null) client.SurroundingTextChanged -= QueryClient;
            probe.Close();
        }
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
