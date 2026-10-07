using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Iseberg.Editor;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;

namespace Runspace.Desktop.Tests;

public sealed partial class ConsoleTests
{
    [AvaloniaFact]
    public async Task FocusedEditorImeRemainsValidDuringNewAndOpenDocumentReplacement()
    {
        using var directory = new ScriptDirectory();
        await File.WriteAllTextAsync(directory.File(), "# opened fixture\n# second line");
        var interaction = new ScriptInteraction { Choice = UnsavedScriptChoice.Discard, OpenPath = directory.File() };
        var session = new FixtureSession();
        var window = new MainWindow(session, false, null, interaction);
        window.Show();
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
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var editor = window.FindControl<PowerShellEditorControl>("ScriptEditor")!;
            window.FindControl<TabControl>("DocumentTabs")!.SelectedItem = window.FindControl<TabItem>("ScriptEditorTab");
            window.UpdateLayout();
            editor.Document.Text = "# original fixture\n# second line";
            Assert.True(editor.FocusEditor());
            editor.CaretOffset = 4;
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.TextEditor.TextArea.RaiseEvent(request);
            client = Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
            client.SurroundingTextChanged += QueryClient;
            var originalDocument = editor.Document;
            Assert.True(await window.ScriptWorkspace.NewAsync());
            Assert.NotSame(originalDocument, editor.Document);
            Assert.Equal("# original fixture\n# second line", originalDocument.Text);
            Assert.Equal(string.Empty, client.SurroundingText);
            Assert.True(await window.ScriptWorkspace.OpenAsync());
            Assert.Equal("# opened fixture\n# second line", editor.Document.Text);
            Assert.False(editor.IsReadOnly);
            Assert.True(editor.FocusEditor());
            editor.CaretOffset = 4;
            Assert.Equal("# opened fixture", client.SurroundingText);
            Assert.True(notifications > 0);
        }
        finally
        {
            if (client is not null) client.SurroundingTextChanged -= QueryClient;
            window.Close();
        }
        await UntilAsync(() => session.Disposed);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FocusedEditorImeSurroundingTextRemainsValidOnTabDetachAndClose(bool close, bool readOnly)
    {
        var interaction = new ScriptInteraction { Choice = UnsavedScriptChoice.Discard };
        var session = new FixtureSession();
        var window = new MainWindow(session, false, null, interaction);
        window.Show();
        TextInputMethodClient? client = null;
        var notifications = 0;
        void ReadSurroundingText(object? sender, EventArgs args)
        {
            _ = client!.SurroundingText;
            _ = client.Selection;
            notifications++;
        }
        var editor = window.FindControl<PowerShellEditorControl>(readOnly ? "CurrentScriptEditor" : "ScriptEditor")!;
        void StopObserving(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
        {
            if (client is not null) client.SurroundingTextChanged -= ReadSurroundingText;
        }
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var tabs = window.FindControl<TabControl>("DocumentTabs")!;
            var editorTab = window.FindControl<TabItem>(readOnly ? "CurrentScriptTab" : "ScriptEditorTab")!;
            var otherTab = window.FindControl<TabItem>(readOnly ? "ScriptEditorTab" : "CurrentScriptTab")!;
            tabs.SelectedItem = editorTab;
            window.UpdateLayout();
            editor.Document.Text = "# first line\n# second line";
            var document = editor.Document;
            Assert.True(editor.FocusEditor());
            editor.CaretOffset = 0;
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            editor.TextEditor.TextArea.RaiseEvent(request);
            if (readOnly)
                Assert.Null(request.Client);
            else
            {
                client = Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
                Assert.Equal("# first line", client.SurroundingText);
                client.SurroundingTextChanged += ReadSurroundingText;
            }
            editor.TextEditor.TextArea.LostFocus += StopObserving;
            editor.CaretOffset = 4;
            if (!readOnly) Assert.True(notifications > 0);
            if (close)
            {
                window.Close();
                await UntilAsync(() => session.Disposed);
            }
            else
            {
                for (var iteration = 0; iteration < 10; iteration++)
                {
                    tabs.SelectedItem = otherTab;
                    window.UpdateLayout();
                    Assert.False(editor.TextEditor.TextArea.IsFocused);
                    tabs.SelectedItem = editorTab;
                    window.UpdateLayout();
                    Assert.True(editor.FocusEditor());
                    Assert.Same(document, editor.Document);
                    Assert.Equal("# first line\n# second line", editor.Document.Text);
                    Assert.Equal(4, editor.CaretOffset);
                    if (client is not null)
                    {
                        client.SurroundingTextChanged -= ReadSurroundingText;
                        client.SurroundingTextChanged += ReadSurroundingText;
                    }
                    editor.CaretOffset = 5;
                    editor.CaretOffset = 4;
                }
                tabs.SelectedItem = window.FindControl<TabItem>("ResultsTab");
            }
            Assert.False(editor.TextEditor.TextArea.IsFocused);
        }
        finally
        {
            editor.TextEditor.TextArea.LostFocus -= StopObserving;
            if (client is not null) client.SurroundingTextChanged -= ReadSurroundingText;
            if (window.IsVisible) window.Close();
        }
        await UntilAsync(() => session.Disposed);
    }
}
