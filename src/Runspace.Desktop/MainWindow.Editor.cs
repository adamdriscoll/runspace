using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using Iseberg.Editor;

namespace Runspace.Desktop;

public partial class MainWindow
{
    private ScriptEditorWorkspace _scriptWorkspace = null!;
    private readonly TextDocument _currentScriptDocument = new();
    private Task? _editorOperation;
    private bool _confirmingEditorClose;
    internal ScriptEditorWorkspace ScriptWorkspace => _scriptWorkspace;

    private static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void InitializeEditors(IScriptEditorInteraction? interaction)
    {
        _scriptWorkspace = new(interaction ?? new ScriptEditorInteraction(this));
        _scriptWorkspace.Changed += EditorWorkspaceChanged;
        CurrentScriptEditor.Document = _currentScriptDocument;
        _model.PropertyChanged += PreviewScriptChanged;
        ScriptEditor.ErrorOccurred += EditorErrorOccurred;
        CurrentScriptEditor.ErrorOccurred += EditorErrorOccurred;
        ActualThemeVariantChanged += EditorThemeChanged;
        foreach (var editor in new[] { ScriptEditor, CurrentScriptEditor })
        {
            editor.TextEditor.FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono, Consolas, monospace");
            editor.TextEditor.FontSize = 13;
            editor.TextEditor.Bind(AvaloniaEdit.TextEditor.ForegroundProperty, this.GetResourceObservable("ConsoleText"));
            editor.TextEditor.Bind(AvaloniaEdit.TextEditor.BackgroundProperty, this.GetResourceObservable("ConsoleContent"));
            editor.TextEditor.Bind(AvaloniaEdit.TextEditor.LineNumbersForegroundProperty, this.GetResourceObservable("ConsoleMuted"));
            AutomationProperties.SetHelpText(editor.TextEditor, "Text editing only. Semantic diagnostics, completion and execution are unavailable.");
        }
        NewScriptMenu.InputGesture = new KeyGesture(Key.N, CommandModifier);
        OpenScriptMenu.InputGesture = new KeyGesture(Key.O, CommandModifier);
        SaveScriptMenu.InputGesture = new KeyGesture(Key.S, CommandModifier);
        SaveScriptAsMenu.InputGesture = new KeyGesture(Key.S, CommandModifier | KeyModifiers.Shift);
        EditorWorkspaceChanged(this, EventArgs.Empty);
        UpdateEditorContrast();
    }

    private void PreviewScriptChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_model.Script)) return;
        _currentScriptDocument.Text = _model.Script;
        _currentScriptDocument.UndoStack.ClearAll();
    }

    private void EditorWorkspaceChanged(object? sender, EventArgs e)
    {
        ScriptEditor.Document = _scriptWorkspace.Document;
        ScriptEditor.IsReadOnly = _scriptWorkspace.IsBusy || _confirmingEditorClose;
        var name = _scriptWorkspace.Path ?? "Untitled.ps1";
        ScriptEditorStatus.Text = $"{name} | {(_scriptWorkspace.IsDirty ? "Unsaved changes" : "No unsaved changes")} | {_scriptWorkspace.Status}";
        ScriptEditorTab.Header = _scriptWorkspace.IsDirty ? "Script Editor *" : "Script Editor";
        foreach (var button in new[] { NewScriptButton, OpenScriptButton, SaveScriptButton, SaveScriptAsButton })
            button.IsEnabled = !_scriptWorkspace.IsBusy && !_confirmingEditorClose;
        foreach (var menu in new[] { NewScriptMenu, OpenScriptMenu, SaveScriptMenu, SaveScriptAsMenu })
            menu.IsEnabled = !_scriptWorkspace.IsBusy && !_confirmingEditorClose;
    }

    private async void EditorErrorOccurred(object? sender, EditorErrorEventArgs e) =>
        await Dialogs.MessageAsync(this, "Script editor error", $"{e.Operation}: {e.Exception.Message}");

    private void EditorThemeChanged(object? sender, EventArgs e) => UpdateEditorContrast();

    private void UpdateEditorContrast()
    {
        var highlighting = ActualThemeVariant != App.HighContrastTheme;
        ScriptEditor.EnableSyntaxHighlighting = highlighting;
        CurrentScriptEditor.EnableSyntaxHighlighting = highlighting;
    }

    private void FocusScriptEditor()
    {
        DocumentTabs.SelectedItem = ScriptEditorTab;
        ScriptEditor.FocusEditor();
    }

    private async Task RunEditorOperationAsync(Func<Task<bool>> operation)
    {
        if (_confirmingEditorClose || _scriptWorkspace.IsBusy || _closing) return;
        FocusScriptEditor();
        _editorOperation = operation();
        try { await _editorOperation; }
        finally
        {
            _editorOperation = null;
            if (!_closing && !_confirmingEditorClose) ScriptEditor.FocusEditor();
        }
    }

    private async void NewScriptClick(object? sender, RoutedEventArgs e) => await RunEditorOperationAsync(_scriptWorkspace.NewAsync);
    private async void OpenScriptClick(object? sender, RoutedEventArgs e) => await RunEditorOperationAsync(_scriptWorkspace.OpenAsync);
    private async void SaveScriptClick(object? sender, RoutedEventArgs e) => await RunEditorOperationAsync(() => _scriptWorkspace.SaveAsync());
    private async void SaveScriptAsClick(object? sender, RoutedEventArgs e) => await RunEditorOperationAsync(() => _scriptWorkspace.SaveAsync(true));
    private void ScriptEditorClick(object? sender, RoutedEventArgs e) => FocusScriptEditor();
    private void FindScriptClick(object? sender, RoutedEventArgs e) { FocusScriptEditor(); ScriptEditor.ShowFind(); }

    private async Task<bool> HandleEditorKeyAsync(KeyEventArgs e)
    {
        var source = e.Source as Visual;
        bool Within(Visual root) => source is not null && (ReferenceEquals(root, source) || root.IsVisualAncestorOf(source));
        var focusedEditor = Within(ScriptEditingPane) || Within(ScriptEditorTab) ? ScriptEditor :
            Within(CurrentScriptEditor) || Within(CurrentScriptTab) ? CurrentScriptEditor : null;
        var modifiers = e.KeyModifiers;
        if (focusedEditor is not null && (e.Key is Key.F5 or Key.F8 or Key.Pause || modifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Left or Key.Right))
        {
            e.Handled = true;
            return true;
        }
        if (modifiers == CommandModifier && e.Key == Key.F && focusedEditor is not null)
        {
            e.Handled = true;
            focusedEditor.ShowFind();
            return true;
        }
        if (modifiers == CommandModifier && e.Key is Key.N or Key.O or Key.S ||
            modifiers == (CommandModifier | KeyModifiers.Shift) && e.Key == Key.S)
        {
            e.Handled = true;
            await RunEditorOperationAsync(e.Key switch
            {
                Key.N => _scriptWorkspace.NewAsync,
                Key.O => _scriptWorkspace.OpenAsync,
                _ => () => _scriptWorkspace.SaveAsync(modifiers.HasFlag(KeyModifiers.Shift))
            });
            return true;
        }
        return false;
    }

    private void DisposeEditors()
    {
        _model.PropertyChanged -= PreviewScriptChanged;
        _scriptWorkspace.Changed -= EditorWorkspaceChanged;
        ActualThemeVariantChanged -= EditorThemeChanged;
        ScriptEditor.ErrorOccurred -= EditorErrorOccurred;
        CurrentScriptEditor.ErrorOccurred -= EditorErrorOccurred;
        ScriptEditor.Dispose();
        CurrentScriptEditor.Dispose();
        _scriptWorkspace.Dispose();
    }
}
