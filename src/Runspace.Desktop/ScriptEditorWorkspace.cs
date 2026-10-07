using System.Text;
using Avalonia.Threading;
using AvaloniaEdit.Document;

namespace Runspace.Desktop;

internal enum UnsavedScriptChoice { Cancel, Save, Discard }

internal interface IScriptEditorInteraction
{
    Task<string?> PickOpenAsync();
    Task<string?> PickSaveAsync(string? currentPath);
    Task<UnsavedScriptChoice> ConfirmUnsavedAsync(string name);
    Task<bool> ConfirmOverwriteAsync(string path);
    Task ReportErrorAsync(string message);
}

internal sealed class ScriptEditorWorkspace : IDisposable
{
    private readonly IScriptEditorInteraction _interaction;
    private ScriptFile? _file;
    private string _savedText = string.Empty;
    private bool _disposed;
    public TextDocument Document { get; private set; } = new();
    public string? Path => _file?.Path;
    public bool IsDirty => Document.Text != _savedText;
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "New script - not saved";
    public event EventHandler? Changed;

    public ScriptEditorWorkspace(IScriptEditorInteraction interaction)
    {
        _interaction = interaction;
        Document.TextChanged += DocumentChanged;
    }

    private void DocumentChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public Task<bool> NewAsync() => OperateAsync(async () =>
    {
        if (!await ProtectUnsavedAsync()) return false;
        ReplaceDocument(null);
        Status = "New script - not saved";
        return true;
    });

    public Task<bool> OpenAsync() => OperateAsync(async () =>
    {
        var path = await _interaction.PickOpenAsync();
        if (path is null) return false;
        // Read before offering to discard; a failed read cannot destroy the current edits.
        await ScriptFileStore.ReadAsync(path);
        if (!await ProtectUnsavedAsync()) return false;
        var file = await ScriptFileStore.ReadAsync(path);
        ReplaceDocument(file);
        Status = "Opened script";
        return true;
    });

    public Task<bool> SaveAsync(bool saveAs = false) => OperateAsync(() => SaveCoreAsync(saveAs));
    public Task<bool> CanCloseAsync() => OperateAsync(ProtectUnsavedAsync);

    private async Task<bool> ProtectUnsavedAsync()
    {
        if (!IsDirty) return true;
        return await _interaction.ConfirmUnsavedAsync(Path is null ? "Untitled.ps1" : System.IO.Path.GetFileName(Path)) switch
        {
            UnsavedScriptChoice.Discard => true,
            UnsavedScriptChoice.Save => await SaveCoreAsync(false),
            _ => false
        };
    }

    private async Task<bool> SaveCoreAsync(bool saveAs)
    {
        var path = !saveAs && Path is not null ? Path : await _interaction.PickSaveAsync(Path);
        if (path is null) return false;
        path = System.IO.Path.GetFullPath(path);
        var sameFile = Path is not null && string.Equals(path, Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        byte[]? expectedHash = sameFile ? _file!.Hash : null;
        if (!sameFile && await Task.Run(() => File.Exists(path)))
        {
            var existing = await ScriptFileStore.ReadAsync(path);
            if (!await _interaction.ConfirmOverwriteAsync(path)) return false;
            expectedHash = existing.Hash;
        }
        var text = Document.Text;
        var saved = await ScriptFileStore.WriteAsync(path, text, _file?.Encoding ?? ScriptFileStore.DefaultEncoding,
            _file?.Preamble ?? [], expectedHash);
        _file = saved;
        _savedText = text;
        Status = "Saved script";
        return true;
    }

    private void ReplaceDocument(ScriptFile? file)
    {
        Document.TextChanged -= DocumentChanged;
        _file = file;
        _savedText = file?.Text ?? string.Empty;
        Document = new TextDocument(_savedText);
        Document.TextChanged += DocumentChanged;
    }

    private async Task<bool> OperateAsync(Func<Task<bool>> operation)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy) return false;
        IsBusy = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try { return await operation(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or EncoderFallbackException or ArgumentException or NotSupportedException)
        {
            Status = "Script file operation failed; current document retained.";
            await _interaction.ReportErrorAsync(exception.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        Document.TextChanged -= DocumentChanged;
        Changed = null;
    }
}
