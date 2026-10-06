using System.Globalization;

namespace Runspace.Core;

public enum ResourceKind
{
    Overview, Processes, ProcessModules, ProcessThreads, Services, EventLogs, EventEntries, NetworkProperties, NetworkInterfaces, ManagedComputers,
    Drives, Providers, ProviderPath, Registry, Shares, LocalUsers, LocalGroups, GroupMembers,
    WmiNamespaces, WmiClasses, WmiInstances, Environment
}

public sealed record ConsoleNode(
    string Id,
    string Name,
    ResourceKind Kind,
    string Description,
    string? Path = null,
    bool WindowsOnly = false,
    string? ProviderName = null);

public enum ColumnKind { Text, Number, Bytes, DateTime, Boolean }

public sealed record ConsoleColumn(string Key, string Header, ColumnKind Kind = ColumnKind.Text, double Width = 150);

public sealed record ConsoleCell(object? Value, string Display, string? Error = null)
{
    public static ConsoleCell From(object? value, ColumnKind kind = ColumnKind.Text)
    {
        if (value is null)
            return new(null, "(null)");

        var display = kind switch
        {
            ColumnKind.Bytes when value is IConvertible number => FormatBytes(number.ToDouble(CultureInfo.InvariantCulture)),
            ColumnKind.DateTime when value is DateTime date => date.ToString("g", CultureInfo.CurrentCulture),
            ColumnKind.Number when value is double number => number.ToString("0.##", CultureInfo.CurrentCulture),
            ColumnKind.Number when value is float number => number.ToString("0.##", CultureInfo.CurrentCulture),
            ColumnKind.Number when value is decimal number => number.ToString("0.##", CultureInfo.CurrentCulture),
            _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
        };
        return new(value, display);
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (Math.Abs(bytes) >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return $"{bytes:N1} {units[unit]}";
    }
}

public sealed record ConsoleRow(
    Guid Handle,
    IReadOnlyDictionary<string, ConsoleCell> Cells,
    ConsoleNode? RelatedNode = null)
{
    public string SearchText => string.Join(" ", Cells.Values.Select(cell => cell.Display));
    public string Label => Cells.Values.FirstOrDefault()?.Display ?? Handle.ToString();
}

public enum InvocationOutcome { Completed, CompletedWithErrors, Failed, Cancelled }

public sealed record DiagnosticRecord(DateTimeOffset Timestamp, string Stream, string Message);
public sealed record ObjectProperty(string Name, string Type, string Value, string? Error = null);
public sealed record ProviderInfo(string Name, string Capabilities);

public sealed record ConsoleResult(
    Guid Id,
    IReadOnlyList<ConsoleColumn> Columns,
    IReadOnlyList<ConsoleRow> Rows,
    string Script,
    IReadOnlyList<DiagnosticRecord> Diagnostics,
    TimeSpan Duration,
    InvocationOutcome Outcome);

public enum ConsoleActionId
{
    Properties, Copy, Export, Browse, StopProcess, StartService, StopService, RestartService,
    AddDrive, RemoveDrive, SetValue, RemoveItem, ProcessModules, ProcessThreads, SetProcessPriority, StartProcess
}

public sealed record ActionParameter(string Name, string Label, bool Required = true, string? DefaultValue = null);

public sealed record ConsoleAction(
    ConsoleActionId Id,
    string Name,
    string Group,
    string Description,
    bool IsEnabled,
    bool RequiresConfirmation = false,
    IReadOnlyList<ActionParameter>? Parameters = null);

public interface IConsoleSession : IAsyncDisposable
{
    string RuntimeVersion { get; }
    Task<ConsoleResult> QueryAsync(ConsoleNode node, CancellationToken cancellationToken = default);
    Task<ConsoleResult> ExecuteAsync(ConsoleActionId action, Guid resultId, IReadOnlyList<Guid> selection,
        IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ObjectProperty>> InspectAsync(Guid resultId, Guid handle, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConsoleNode>> GetDriveNodesAsync(CancellationToken cancellationToken = default);
    void ReleaseResult(Guid resultId);
}
