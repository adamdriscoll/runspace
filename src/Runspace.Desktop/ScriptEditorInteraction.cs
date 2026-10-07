using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Runspace.Desktop;

internal sealed class ScriptEditorInteraction(MainWindow owner) : IScriptEditorInteraction
{
    private static FilePickerFileType Scripts { get; } = new("PowerShell script") { Patterns = ["*.ps1"] };

    public async Task<string?> PickOpenAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open script for editing (does not run it)", AllowMultiple = false, FileTypeFilter = [Scripts, FilePickerFileTypes.All]
        });
        if (files.Count == 0) return null;
        using var file = files[0];
        return LocalPath(file);
    }

    public async Task<string?> PickSaveAsync(string? currentPath)
    {
        using var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save script", SuggestedFileName = currentPath is null ? "Untitled.ps1" : Path.GetFileName(currentPath),
            DefaultExtension = "ps1", FileTypeChoices = [Scripts], ShowOverwritePrompt = false
        });
        return file is null ? null : LocalPath(file);
    }

    private static string LocalPath(IStorageFile file) => file.TryGetLocalPath() ??
        throw new NotSupportedException("Script editing requires a local filesystem path. Save or download this file locally first.");

    public Task<UnsavedScriptChoice> ConfirmUnsavedAsync(string name) => Dialogs.UnsavedScriptAsync(owner, name);
    public Task<bool> ConfirmOverwriteAsync(string path) => Dialogs.OverwriteScriptAsync(owner, path);
    public Task ReportErrorAsync(string message) => Dialogs.MessageAsync(owner, "Script file error", message);
}
