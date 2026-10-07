using System.Management.Automation.Runspaces;
using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia;
using Avalonia.VisualTree;
using AvaloniaEdit.Search;
using Iseberg.Editor;
using Runspace.Core;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;
using RuntimeRunspace = System.Management.Automation.Runspaces.Runspace;

namespace Runspace.Desktop.Tests;

public sealed partial class ConsoleTests
{
    private sealed class ScriptInteraction : IScriptEditorInteraction
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }
        public TaskCompletionSource<string?>? OpenGate { get; set; }
        public UnsavedScriptChoice Choice { get; set; } = UnsavedScriptChoice.Cancel;
        public bool Overwrite { get; set; }
        public int Prompts { get; private set; }
        public List<string> Errors { get; } = [];
        public Task<string?> PickOpenAsync() => OpenGate?.Task ?? Task.FromResult(OpenPath);
        public Task<string?> PickSaveAsync(string? currentPath) => Task.FromResult(SavePath);
        public Task<UnsavedScriptChoice> ConfirmUnsavedAsync(string name) { Prompts++; return Task.FromResult(Choice); }
        public Task<bool> ConfirmOverwriteAsync(string path) => Task.FromResult(Overwrite);
        public Task ReportErrorAsync(string message) { Errors.Add(message); return Task.CompletedTask; }
    }

    private sealed class ScriptDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runspace-script-" + Guid.NewGuid().ToString("N"));
        public ScriptDirectory() => Directory.CreateDirectory(Path);
        public string File(string name = "fixture.ps1") => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, true);
    }

    [AvaloniaFact]
    public async Task ScriptEditorNewOpenSaveAsPreserveEncodingNewlinesAndUndoDirtyState()
    {
        using var directory = new ScriptDirectory();
        var path = directory.File();
        var text = "# harmless fixture\r\nGet-Date\r\n";
        var encoding = new UnicodeEncoding(false, true, true);
        await File.WriteAllTextAsync(path, text, encoding);
        var interaction = new ScriptInteraction { OpenPath = path, SavePath = directory.File("copy.ps1") };
        using var workspace = new ScriptEditorWorkspace(interaction);
        Assert.True(await workspace.OpenAsync());
        Assert.Equal(text, workspace.Document.Text);
        Assert.Equal(path, workspace.Path);
        Assert.False(workspace.IsDirty);
        workspace.Document.Insert(0, "# edited\r\n");
        Assert.True(workspace.IsDirty);
        workspace.Document.UndoStack.Undo();
        Assert.False(workspace.IsDirty);
        workspace.Document.Insert(0, "# edited\r\n");
        Assert.True(await workspace.SaveAsync());
        Assert.False(workspace.IsDirty);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.Equal(workspace.Document.Text, encoding.GetString(bytes, 2, bytes.Length - 2));
        Assert.True(await workspace.SaveAsync(true));
        Assert.Equal(interaction.SavePath, workspace.Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(interaction.SavePath!));
        Assert.True(await workspace.NewAsync());
        Assert.Null(workspace.Path);
        Assert.Equal(string.Empty, workspace.Document.Text);
        Assert.False(workspace.Document.UndoStack.CanUndo);
        workspace.Document.Text = "# new";
        interaction.SavePath = directory.File("new.ps1");
        Assert.True(await workspace.SaveAsync());
        Assert.Equal(Encoding.UTF8.GetBytes("# new"), await File.ReadAllBytesAsync(interaction.SavePath));
        Assert.Empty(interaction.Errors);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ScriptEditorPreservesEverySupportedBomEncoding(int index)
    {
        Encoding[] encodings =
        [
            new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true)
        ];
        using var directory = new ScriptDirectory();
        await File.WriteAllTextAsync(directory.File(), "# benign\r\n# mixed\n# unicode \u00e9\r\n", encodings[index]);
        var interaction = new ScriptInteraction { OpenPath = directory.File(), SavePath = directory.File("copy.ps1") };
        using var workspace = new ScriptEditorWorkspace(interaction);
        Assert.True(await workspace.OpenAsync());
        Assert.True(await workspace.SaveAsync(true));
        Assert.Equal(await File.ReadAllBytesAsync(directory.File()), await File.ReadAllBytesAsync(interaction.SavePath!));
    }

    [AvaloniaFact]
    public async Task ScriptEditorUnixSavesKeepPrivatePermissionsAndRejectSymbolicLinkReplacement()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new ScriptDirectory();
        var path = directory.File();
        await File.WriteAllTextAsync(path, "# original");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var interaction = new ScriptInteraction { OpenPath = path };
        using var workspace = new ScriptEditorWorkspace(interaction);
        Assert.True(await workspace.OpenAsync());
        workspace.Document.Text = "# saved";
        Assert.True(await workspace.SaveAsync());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        var link = directory.File("link.ps1");
        File.CreateSymbolicLink(link, path);
        interaction.OpenPath = link;
        Assert.True(await workspace.OpenAsync());
        workspace.Document.Text = "# changed link";
        Assert.False(await workspace.SaveAsync());
        Assert.Contains("symbolic-link", Assert.Single(interaction.Errors));
        Assert.Equal("# saved", await File.ReadAllTextAsync(path));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptEditorCancelAndSaveCancellationKeepOriginalDocument(bool save)
    {
        using var directory = new ScriptDirectory();
        await File.WriteAllTextAsync(directory.File(), "# next");
        var interaction = new ScriptInteraction { OpenPath = directory.File(), Choice = save ? UnsavedScriptChoice.Save : UnsavedScriptChoice.Cancel };
        using var workspace = new ScriptEditorWorkspace(interaction);
        var original = workspace.Document;
        original.Text = "# unsaved secret fixture";
        Assert.False(await workspace.NewAsync());
        Assert.False(await workspace.OpenAsync());
        Assert.False(await workspace.CanCloseAsync());
        Assert.Same(original, workspace.Document);
        Assert.Equal("# unsaved secret fixture", workspace.Document.Text);
        Assert.Null(workspace.Path);
        Assert.True(workspace.IsDirty);
        Assert.Empty(interaction.Errors);
        Assert.Equal(3, interaction.Prompts);
    }

    [AvaloniaFact]
    public async Task ScriptEditorFailedReadsWritesAndExternalChangesAreVisibleAndNonDestructive()
    {
        using var directory = new ScriptDirectory();
        var path = directory.File();
        await File.WriteAllTextAsync(path, "# original");
        var interaction = new ScriptInteraction { OpenPath = path };
        using var workspace = new ScriptEditorWorkspace(interaction);
        Assert.True(await workspace.OpenAsync());
        var original = workspace.Document;
        original.Text = "# modified";
        interaction.OpenPath = directory.File("missing.ps1");
        Assert.False(await workspace.OpenAsync());
        Assert.Equal(0, interaction.Prompts);
        interaction.Choice = UnsavedScriptChoice.Save;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await workspace.NewAsync());
            Assert.False(await workspace.CanCloseAsync());
            Assert.Same(original, workspace.Document);
            Assert.Equal(path, workspace.Path);
            Assert.True(workspace.IsDirty);
        }
        Assert.Equal("# original", await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "# externally replaced");
        Assert.False(await workspace.SaveAsync());
        Assert.Contains("changed on disk", interaction.Errors.Last());
        Assert.Equal("# externally replaced", await File.ReadAllTextAsync(path));
        Assert.True(workspace.IsDirty);
        Assert.Same(original, workspace.Document);
        Assert.Equal("# modified", workspace.Document.Text);
        Assert.Equal(4, interaction.Errors.Count);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [AvaloniaFact]
    public async Task ScriptEditorRejectsUnsupportedEncodingAndProtectsOverwrite()
    {
        using var directory = new ScriptDirectory();
        await File.WriteAllBytesAsync(directory.File("invalid.ps1"), [0xff, 0x80]);
        var interaction = new ScriptInteraction { OpenPath = directory.File("invalid.ps1") };
        using var workspace = new ScriptEditorWorkspace(interaction);
        Assert.False(await workspace.OpenAsync());
        Assert.Single(interaction.Errors);
        workspace.Document.Text = "# edited";
        var existing = directory.File();
        await File.WriteAllTextAsync(existing, "# original");
        interaction.SavePath = existing;
        Assert.False(await workspace.SaveAsync(true));
        Assert.Null(workspace.Path);
        Assert.True(workspace.IsDirty);
        Assert.Equal("# original", await File.ReadAllTextAsync(existing));
        interaction.Overwrite = true;
        Assert.True(await workspace.SaveAsync(true));
        Assert.Equal("# edited", await File.ReadAllTextAsync(existing));
    }

    [AvaloniaFact]
    public async Task ScriptEditorOpeningSameFileAfterSaveDoesNotReloadStaleText()
    {
        using var directory = new ScriptDirectory();
        await File.WriteAllTextAsync(directory.File(), "# original");
        var interaction = new ScriptInteraction { OpenPath = directory.File(), Choice = UnsavedScriptChoice.Save };
        using var workspace = new ScriptEditorWorkspace(interaction);
        Assert.True(await workspace.OpenAsync());
        workspace.Document.Text = "# modified";
        Assert.True(await workspace.OpenAsync());
        Assert.Equal("# modified", workspace.Document.Text);
        Assert.False(workspace.IsDirty);
        workspace.Document.Text = "# discard";
        interaction.Choice = UnsavedScriptChoice.Discard;
        Assert.True(await workspace.NewAsync());
        Assert.Equal(string.Empty, workspace.Document.Text);
    }

    [AvaloniaFact]
    public async Task ScriptEditorPublishedControlsRenderIndependentlyWithoutEngineSideEffects()
    {
        var session = new FixtureSession();
        var interaction = new ScriptInteraction { Choice = UnsavedScriptChoice.Discard };
        var window = new MainWindow(session, false, interaction);
        window.Show();
        try
        {
            var model = (ConsoleViewModel)window.DataContext!;
            await UntilAsync(() => !model.IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
            var selection = grid.SelectedItem;
            var items = grid.ItemsSource;
            var history = model.History;
            var queries = session.Queries;
            var driveReads = session.DriveReads;
            var originalDefault = RuntimeRunspace.DefaultRunspace;
            using var sentinel = RunspaceFactory.CreateRunspace();
            RuntimeRunspace.DefaultRunspace = sentinel;
            try
            {
                var tabs = window.FindControl<TabControl>("DocumentTabs")!;
                var editor = window.FindControl<PowerShellEditorControl>("ScriptEditor")!;
                var preview = window.FindControl<PowerShellEditorControl>("CurrentScriptEditor")!;
                tabs.SelectedItem = window.FindControl<TabItem>("ScriptEditorTab");
                window.UpdateLayout();
                Assert.True(editor.FocusEditor());
                window.KeyTextInput("configuration Fixture { Node localhost { } }\r\nGet-Date");
                Assert.Contains("configuration", editor.Document.Text);
                Assert.True(window.ScriptWorkspace.IsDirty);
                Assert.NotSame(editor.Document, preview.Document);
                Assert.Equal(EditorAnalysisState.Unavailable, editor.Analysis.State);
                Assert.Null(editor.AnalysisProvider);
                Assert.Null(editor.CompletionProvider);
                Assert.False(editor.EnableExecutionGestures);
                model.Script = "# current administration representation";
                Assert.Equal(model.Script, preview.Document.Text);
                Assert.DoesNotContain("administration", editor.Document.Text);
                foreach (var key in new[] { Key.F5, Key.F8, Key.Pause })
                {
                    Press(window, key, key == Key.Pause ? RawInputModifiers.Control : RawInputModifiers.None);
                }
                Assert.Same(sentinel, RuntimeRunspace.DefaultRunspace);
                Assert.Equal(RunspaceState.BeforeOpen, sentinel.RunspaceStateInfo.State);
                using var next = RunspaceFactory.CreateRunspace();
                Assert.Equal(sentinel.Id + 1, next.Id);
                Assert.Equal(queries, session.Queries);
                Assert.Equal(driveReads, session.DriveReads);
                Assert.Null(session.LastOutcome);
                Assert.Equal(history, model.History);
                Assert.DoesNotContain("configuration", model.History + model.Diagnostics);
                editor.ShowFind();
                Assert.False(editor.GetVisualDescendants().OfType<SearchPanel>().Single().IsClosed);
                using var image = new RenderTargetBitmap(new PixelSize(800, 600));
                image.Render(window);
                Assert.True(editor.Bounds.Width > 100 && editor.Bounds.Height > 100);
                tabs.SelectedItem = window.FindControl<TabItem>("CurrentScriptTab");
                window.UpdateLayout();
                Assert.True(preview.FocusEditor());
                var previewText = preview.Document.Text;
                window.KeyTextInput("not allowed");
                Assert.Equal(previewText, preview.Document.Text);
                await window.Clipboard!.SetTextAsync("must not paste");
                preview.TextEditor.Paste();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                Assert.Equal(previewText, preview.Document.Text);
                var peer = ControlAutomationPeer.CreatePeerForElement(preview.TextEditor);
                var value = Assert.IsAssignableFrom<IValueProvider>(peer);
                Assert.True(value.IsReadOnly);
                Assert.Throws<InvalidOperationException>(() => value.SetValue("not allowed"));
                Assert.Equal(previewText, preview.Document.Text);
                tabs.SelectedItem = window.FindControl<TabItem>("ResultsTab");
                window.UpdateLayout();
                Assert.Same(items, grid.ItemsSource);
                Assert.Same(selection, grid.SelectedItem);
                Assert.Equal(history, model.History);
                Assert.False(session.Disposed);
                grid.Focus();
                Press(window, Key.F5);
                await UntilAsync(() => session.Queries == queries + 1 && !model.IsBusy);
            }
            finally { RuntimeRunspace.DefaultRunspace = originalDefault; }
        }
        finally { window.Close(); }
        await UntilAsync(() => session.Disposed);
        Assert.Throws<ObjectDisposedException>(() => window.FindControl<PowerShellEditorControl>("ScriptEditor")!.CaptureText());
        Assert.Throws<ObjectDisposedException>(() => window.FindControl<PowerShellEditorControl>("CurrentScriptEditor")!.CaptureText());
    }

    [AvaloniaFact]
    public async Task ScriptEditorDoesNotStealConsoleFocusedShortcuts()
    {
        var session = new FixtureSession();
        var interaction = new ScriptInteraction { Choice = UnsavedScriptChoice.Discard };
        var window = new MainWindow(session, false, interaction);
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var tabs = window.FindControl<TabControl>("DocumentTabs")!;
            var editor = window.FindControl<PowerShellEditorControl>("ScriptEditor")!;
            editor.Document.Text = "# keep edits";
            tabs.SelectedItem = window.FindControl<TabItem>("ScriptEditorTab");
            window.UpdateLayout();
            var queries = session.Queries;
            window.FindControl<TreeView>("NavigationTree")!.Focus();
            Press(window, Key.F5);
            await UntilAsync(() => session.Queries == queries + 1);
            Assert.Equal("# keep edits", editor.Document.Text);
            Assert.Same(window.FindControl<TabItem>("ResultsTab"), tabs.SelectedItem);
            tabs.SelectedItem = window.FindControl<TabItem>("ScriptEditorTab");
            window.UpdateLayout();
            window.FindControl<Button>("FindScriptButton")!.Focus();
            var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            Press(window, Key.F, modifier);
            Assert.False(editor.GetVisualDescendants().OfType<SearchPanel>().Single().IsClosed);
            Assert.Same(window.FindControl<TabItem>("ScriptEditorTab"), tabs.SelectedItem);
            Press(window, Key.F5);
            Assert.Equal(queries + 1, session.Queries);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ScriptEditorFileShortcutsSaveCloseAndReadOnlyLockAreWired()
    {
        using var directory = new ScriptDirectory();
        await File.WriteAllTextAsync(directory.File(), "# opened\r\n");
        var interaction = new ScriptInteraction
        {
            OpenPath = directory.File(), SavePath = directory.File("saved-as.ps1"), Choice = UnsavedScriptChoice.Save,
            OpenGate = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var session = new FixtureSession();
        var window = new MainWindow(session, false, interaction);
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.FindControl<TextBox>("FilterBox")!.Focus();
            Press(window, Key.O, modifier);
            Assert.True(window.FindControl<PowerShellEditorControl>("ScriptEditor")!.IsReadOnly);
            interaction.OpenGate.SetResult(interaction.OpenPath);
            await UntilAsync(() => !window.ScriptWorkspace.IsBusy);
            var editor = window.FindControl<PowerShellEditorControl>("ScriptEditor")!;
            Assert.Equal("# opened\r\n", editor.Document.Text);
            Assert.False(editor.IsReadOnly);
            Assert.Same(window.FindControl<TabItem>("ScriptEditorTab"), window.FindControl<TabControl>("DocumentTabs")!.SelectedItem);
            editor.Document.Text = "# saved as";
            Press(window, Key.S, modifier | RawInputModifiers.Shift);
            await UntilAsync(() => !window.ScriptWorkspace.IsBusy);
            Assert.Equal(interaction.SavePath, window.ScriptWorkspace.Path);
            Assert.Equal("# saved as", await File.ReadAllTextAsync(interaction.SavePath!));
            editor.Document.Text = "# final close save";
            window.Close();
            await UntilAsync(() => session.Disposed);
            Assert.Equal("# final close save", await File.ReadAllTextAsync(interaction.SavePath!));
            Assert.Equal("# opened\r\n", await File.ReadAllTextAsync(directory.File()));
            Assert.Empty(interaction.Errors);
        }
        finally { if (window.IsVisible) { interaction.Choice = UnsavedScriptChoice.Discard; window.Close(); } }
    }

    [AvaloniaFact]
    public async Task ScriptEditorShortcutsUndoFindContrastAndCloseCancelStayLocal()
    {
        var interaction = new ScriptInteraction();
        var window = new MainWindow(new FixtureSession(), false, interaction);
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var tabs = window.FindControl<TabControl>("DocumentTabs")!;
            var editor = window.FindControl<PowerShellEditorControl>("ScriptEditor")!;
            tabs.SelectedItem = window.FindControl<TabItem>("ScriptEditorTab");
            window.UpdateLayout();
            editor.FocusEditor();
            window.KeyTextInput("# benign fixture");
            var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            Press(window, Key.Z, modifier);
            Assert.Equal(string.Empty, editor.Document.Text);
            Assert.False(window.ScriptWorkspace.IsDirty);
            window.KeyTextInput("# unsaved");
            Press(window, Key.F, modifier);
            Assert.Same(window.FindControl<TabItem>("ScriptEditorTab"), tabs.SelectedItem);
            Assert.False(editor.GetVisualDescendants().OfType<SearchPanel>().Single().IsClosed);
            window.RequestedThemeVariant = App.HighContrastTheme;
            Assert.False(editor.EnableSyntaxHighlighting);
            Assert.False(window.FindControl<PowerShellEditorControl>("CurrentScriptEditor")!.EnableSyntaxHighlighting);
            Assert.Equal(Avalonia.Media.Colors.White, ((Avalonia.Media.ISolidColorBrush)editor.TextEditor.Foreground!).Color);
            window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Assert.True(editor.EnableSyntaxHighlighting);
            window.Close();
            await UntilAsync(() => !window.ScriptWorkspace.IsBusy);
            Assert.True(window.IsVisible);
            Assert.False(editor.IsReadOnly);
            interaction.Choice = UnsavedScriptChoice.Discard;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ScriptEditorRealUnsavedDialogCancelDiscardAndSaveCancellationProtectClose()
    {
        var window = new MainWindow(new FixtureSession());
        window.Show();
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.ScriptWorkspace.Document.Text = "# unsaved fixture";
            window.Close();
            var dialog = window.OwnedWindows.Single();
            Assert.Equal("UnsavedScriptDialog", dialog.Name);
            Click(Named<Button>(dialog, "ScriptCancel"));
            await UntilAsync(() => !window.ScriptWorkspace.IsBusy);
            Assert.True(window.IsVisible);
            Assert.False(window.FindControl<PowerShellEditorControl>("ScriptEditor")!.IsReadOnly);
            window.Close();
            dialog = window.OwnedWindows.Single();
            Click(Named<Button>(dialog, "ScriptDiscard"));
            await UntilAsync(() => !window.IsVisible);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.ScriptWorkspace.Document.Text = string.Empty;
                window.Close();
            }
        }
    }
}
