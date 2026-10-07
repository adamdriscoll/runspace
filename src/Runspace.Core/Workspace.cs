using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Runspace.Core;

public sealed record ResourceReference(string SessionId, string KitId, string ResourceId)
{
    public static ResourceReference BuiltIn(string resourceId) => new("local", "builtin.local-system", resourceId);
}

public sealed record WorkspaceLayout
{
    public double Width { get; init; } = 1200;
    public double Height { get; init; } = 800;
    public int? X { get; init; }
    public int? Y { get; init; }
    public double NavigationWidth { get; init; } = 220;
    public double ActionsWidth { get; init; } = 220;
    public double DiagnosticsHeight { get; init; } = 160;
    public bool NavigationVisible { get; init; } = true;
    public bool ActionsVisible { get; init; } = true;
    public bool DiagnosticsVisible { get; init; }
    public bool HighContrast { get; init; }
}

public sealed record WorkspaceColumn(string Key, bool Visible, double Width);
public sealed record WorkspaceSort(string Key, bool Descending);
public sealed record WorkspaceView(ResourceReference Reference, IReadOnlyList<WorkspaceColumn> Columns,
    IReadOnlyList<WorkspaceSort> Sorting, string Filter);

public sealed record WorkspaceDocument
{
    public const int CurrentVersion = 1;
    [JsonRequired] public int Version { get; init; } = CurrentVersion;
    [JsonRequired] public WorkspaceLayout Layout { get; init; } = new();
    public ResourceReference? ActiveView { get; init; }
    [JsonRequired] public IReadOnlyList<WorkspaceView> Views { get; init; } = [];
}

public interface IWorkspaceStorage
{
    string? Read(string name);
    void WriteAtomic(string name, string content);
    string Archive(string name);
}

public sealed class FileWorkspaceStorage(string directory) : IWorkspaceStorage
{
    public string? Read(string name)
    {
        var path = Path.Combine(directory, name);
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > WorkspaceStore.MaximumFileBytes)
                throw new InvalidDataException("Workspace data exceeds the 4 MiB limit.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var content = reader.ReadToEnd();
            return content.StartsWith('\uFEFF') ? content[1..] : content;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void WriteAtomic(string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, leaveOpen: true))
                {
                    writer.Write(content);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string Archive(string name)
    {
        var backup = name + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "." +
            Guid.NewGuid().ToString("N") + ".bak";
        File.Move(Path.Combine(directory, name), Path.Combine(directory, backup));
        return Path.Combine(directory, backup);
    }
}

public sealed record WorkspaceLoad(WorkspaceDocument Document, string? RecoveryMessage = null, bool Migrated = false);

public sealed class WorkspaceStore(IWorkspaceStorage storage)
{
    public const string FileName = "workspace.json";
    public const string LegacyFileName = "layout.json";
    public const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    private string? _protectedFile;
    private bool _loaded;
    public bool IsWriteBlocked => _protectedFile is not null;

    public WorkspaceLoad Load()
    {
        _loaded = true;
        var source = FileName;
        try
        {
            var content = Read(source);
            if (content is not null)
            {
                using var json = JsonDocument.Parse(content);
                if (json.RootElement.ValueKind != JsonValueKind.Object ||
                    !json.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out var number) || number != WorkspaceDocument.CurrentVersion)
                    throw new InvalidDataException("The workspace version is missing or unsupported.");
                var workspace = JsonSerializer.Deserialize<WorkspaceDocument>(content, Json)
                    ?? throw new InvalidDataException("The workspace is empty.");
                Validate(workspace);
                _protectedFile = null;
                return new(workspace);
            }
            source = LegacyFileName;
            content = Read(source);
            if (content is null)
            {
                _protectedFile = null;
                return new(new());
            }
            var legacy = JsonSerializer.Deserialize<LegacyLayout>(content, Json)
                ?? throw new InvalidDataException("The saved layout is empty.");
            Positive(legacy.Width, "window width");
            Positive(legacy.Height, "window height");
            Positive(legacy.Left, "navigation width");
            Positive(legacy.Right, "actions width");
            _protectedFile = null;
            return new(new()
            {
                Layout = new()
                {
                    Width = Math.Clamp(legacy.Width, 900, 2000), Height = Math.Clamp(legacy.Height, 600, 1400),
                    NavigationWidth = Math.Clamp(legacy.Left, 160, 350), ActionsWidth = Math.Clamp(legacy.Right, 180, 350)
                }
            }, Migrated: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            _protectedFile = source;
            return new(new(), $"Cannot restore {source}: {exception.Message} The original is protected; " +
                "repair it and choose Reload, or choose Back up and reset in Workspace settings.");
        }
    }

    public void Save(WorkspaceDocument workspace)
    {
        if (!_loaded) throw new InvalidOperationException("Load the workspace before saving.");
        if (IsWriteBlocked) throw new InvalidOperationException("Workspace saving is blocked until the original data is recovered.");
        Validate(workspace);
        var content = JsonSerializer.Serialize(workspace, Json);
        CheckSize(content);
        storage.WriteAtomic(FileName, content);
    }

    private string? Read(string name)
    {
        var content = storage.Read(name);
        if (content is not null) CheckSize(content);
        return content;
    }

    private static void CheckSize(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaximumFileBytes)
            throw new InvalidDataException("Workspace data exceeds the 4 MiB limit.");
    }

    public string BackUpDamagedFile()
    {
        if (_protectedFile is not { } source) throw new InvalidOperationException("No workspace file requires recovery.");
        var backup = storage.Archive(source);
        _protectedFile = null;
        return backup;
    }

    public static IReadOnlyList<ResourceReference> Unresolved(WorkspaceDocument workspace,
        Func<ResourceReference, bool> isAvailable) =>
        workspace.Views.Select(view => view.Reference)
            .Concat(workspace.ActiveView is { } active ? [active] : [])
            .Distinct().Where(reference => !isAvailable(reference)).ToArray();

    public static WorkspaceDocument RemoveReferences(WorkspaceDocument workspace, IEnumerable<ResourceReference> references)
    {
        var removed = references.ToHashSet();
        return workspace with
        {
            ActiveView = workspace.ActiveView is { } active && removed.Contains(active) ? null : workspace.ActiveView,
            Views = workspace.Views.Where(view => !removed.Contains(view.Reference)).ToArray()
        };
    }

    private sealed record LegacyLayout(double Width, double Height, double Left, double Right);

    public static void Validate(WorkspaceDocument workspace)
    {
        if (workspace.Version != WorkspaceDocument.CurrentVersion)
            throw new InvalidDataException("Unsupported workspace version.");
        if (workspace.Layout is not { } layout || workspace.Views is null || workspace.Views.Count > 500)
            throw new InvalidDataException("Invalid workspace layout or view list.");
        Range(layout.Width, 1, 2000, "window width");
        Range(layout.Height, 1, 1400, "window height");
        if (layout.X.HasValue != layout.Y.HasValue) throw new InvalidDataException("Window position must contain both coordinates.");
        Range(layout.NavigationWidth, 160, 350, "navigation width");
        Range(layout.ActionsWidth, 180, 350, "actions width");
        Range(layout.DiagnosticsHeight, 80, 500, "diagnostics height");
        if (workspace.ActiveView is not null) Reference(workspace.ActiveView);
        var references = new HashSet<ResourceReference>();
        foreach (var view in workspace.Views)
        {
            if (view is null) throw new InvalidDataException("A workspace view is empty.");
            Reference(view.Reference);
            if (!references.Add(view.Reference) || view.Columns is null || view.Sorting is null || view.Filter is null ||
                view.Columns.Count > 200 || view.Sorting.Count > 200 || view.Filter.Length > 4096)
                throw new InvalidDataException("Invalid or duplicate view preferences.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in view.Columns)
            {
                if (column is null) throw new InvalidDataException("A column preference is empty.");
                Identifier(column.Key);
                if (!keys.Add(column.Key)) throw new InvalidDataException("Duplicate column preference.");
                Range(column.Width, 40, 1200, "column width");
            }
            if (view.Columns.Count > 0 && !view.Columns.Any(column => column.Visible))
                throw new InvalidDataException("Keep at least one saved column visible.");
            keys.Clear();
            foreach (var sort in view.Sorting)
            {
                if (sort is null) throw new InvalidDataException("A sort preference is empty.");
                Identifier(sort.Key);
                if (!keys.Add(sort.Key)) throw new InvalidDataException("Duplicate sort preference.");
            }
        }
    }

    private static void Reference(ResourceReference reference)
    {
        if (reference is null) throw new InvalidDataException("A resource reference is empty.");
        Identifier(reference.SessionId);
        Identifier(reference.KitId);
        Identifier(reference.ResourceId);
    }

    private static void Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid preference identifier.");
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new InvalidDataException($"Invalid {name}.");
    }

    private static void Range(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException($"Invalid {name}; expected {minimum} to {maximum}.");
    }
}

public sealed record WorkspaceDisplay(int X, int Y, int Width, int Height, double Scaling);
public sealed record WorkspacePlacement(double Width, double Height, int X, int Y)
{
    public static WorkspacePlacement Fit(WorkspaceLayout layout, IReadOnlyList<WorkspaceDisplay> displays)
    {
        if (displays.Count == 0) throw new InvalidOperationException("No display working area is available.");
        if (displays.Any(area => area.Width <= 0 || area.Height <= 0 || !double.IsFinite(area.Scaling) || area.Scaling <= 0))
            throw new InvalidOperationException("A display working area or scale is invalid.");
        var display = displays.FirstOrDefault(area => layout.X is { } x && layout.Y is { } y &&
            x >= area.X && y >= area.Y && x < (long)area.X + area.Width && y < (long)area.Y + area.Height) ?? displays[0];
        var width = Math.Min(Math.Max(900, layout.Width), Math.Max(1, display.Width / display.Scaling - 32));
        var height = Math.Min(Math.Max(600, layout.Height), Math.Max(1, display.Height / display.Scaling - 64));
        var xMaximum = (long)display.X + Math.Max(0, display.Width - (int)Math.Ceiling((width + 16) * display.Scaling));
        var yMaximum = (long)display.Y + Math.Max(0, display.Height - (int)Math.Ceiling((height + 48) * display.Scaling));
        return new(width, height,
            (int)Math.Clamp(layout.X ?? display.X + display.Width / 10, display.X, xMaximum),
            (int)Math.Clamp(layout.Y ?? display.Y + display.Height / 10, display.Y, yMaximum));
    }
}
