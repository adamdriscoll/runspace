using System.Globalization;
using System.Text.Json;
using Runspace.Core;

namespace Runspace.PowerShell;

public sealed record PublishCheck(string Name, string Command, bool Passed, string Detail);

public sealed class PublishedRuntimeProbe
{
    public List<PublishCheck> Checks { get; } = [];

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        PowerShellPayload.Validate(AppContext.BaseDirectory, OperatingSystem.IsWindows());
        Checks.Add(new("PowerShell payload", "Check required manifests, assemblies and native assets", true,
            string.Join(", ", PowerShellPayload.RequiredFiles(OperatingSystem.IsWindows()))));
        await using var session = new PowerShellSession();
        var scratch = Path.Combine(Path.GetTempPath(), "runspace-probe-" + Guid.NewGuid().ToString("N"));
        var key = "RUNSPACE_PUBLISH_PROBE_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "probe.txt"), "published payload");
        Environment.SetEnvironmentVariable(key, "published payload");
        try
        {
            await CheckQueryAsync(session, new("processes", "Processes", ResourceKind.Processes, "Publish validation"),
                result => result.Rows.Any(row => Convert.ToInt32(row.Cells["Id"].Value, CultureInfo.InvariantCulture) == Environment.ProcessId
                    && row.Cells.Values.All(cell => cell.Error is null)),
                "The current benign process must be present.", cancellationToken);
            await CheckQueryAsync(session, new("providers", "Providers", ResourceKind.Providers, "Publish validation"),
                result => new[] { "FileSystem", "Environment", "Alias", "Function", "Variable" }
                    .All(name => result.Rows.Any(row => row.Cells["Name"].Display == name)),
                "Actual built-in providers must be present.", cancellationToken);
            await CheckQueryAsync(session, new("drives", "Drives", ResourceKind.Drives, "Publish validation"),
                result => result.Rows.Any(row => row.RelatedNode?.ProviderName == "FileSystem")
                    && result.Rows.Any(row => row.RelatedNode?.Path == "Env:\\"),
                "Actual FileSystem and Environment drives must be present.", cancellationToken);
            await CheckQueryAsync(session, new("files", "Files", ResourceKind.ProviderPath, "Publish validation", scratch),
                result => result.Rows.Any(row => row.Cells["Name"].Display == "probe.txt"),
                "Browse a literal temporary filesystem path.", cancellationToken);
            await CheckQueryAsync(session, new("environment", "Environment", ResourceKind.Environment, "Publish validation"),
                result => result.Rows.Any(row => row.Cells["Name"].Display == key && row.Cells["Value"].Display == "published payload"),
                "Inspect a synthetic environment entry; do not record other environment values.", cancellationToken);

            const string script = """
                $ErrorActionPreference = 'Stop'
                $modules = @('Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Security')
                if ($IsWindows) { $modules += 'CimCmdlets', 'Microsoft.PowerShell.Diagnostics' }
                foreach ($name in $modules) { Import-Module $name -ErrorAction Stop }
                Add-Type -TypeDefinition 'public static class RunspacePublishMarker { public static int Value => 42; }'
                if ([RunspacePublishMarker]::Value -ne 42) { throw 'Add-Type did not execute the compiled type.' }
                @{
                    PowerShell = $PSVersionTable.PSVersion.ToString()
                    Edition = $PSVersionTable.PSEdition
                    PSHome = $PSHOME
                    Modules = @(Get-Module $modules | ForEach-Object {
                        @{ Name = $_.Name; Version = $_.Version.ToString(); Path = $_.Path }
                    })
                    Assemblies = @(Get-Command Get-Process, ConvertTo-Json, Get-ExecutionPolicy | ForEach-Object {
                        $_.ImplementingType.Assembly.Location
                    })
                    ProfileLoaded = Test-Path Variable:\RunspaceProfileExecuted
                    ExternalPwshPresent = [IO.File]::Exists([IO.Path]::Combine($PSHOME, $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })))
                    AddType = [RunspacePublishMarker]::Value
                } | ConvertTo-Json -Depth 5 -Compress
                """;
            var runtime = await session.InvokeForTestingAsync(script, cancellationToken);
            try
            {
                EnsureCompleted(runtime);
                using var evidence = JsonDocument.Parse(Convert.ToString(runtime.Rows.Single().Cells["Value"].Value, CultureInfo.InvariantCulture)!);
                var root = evidence.RootElement;
                if (root.GetProperty("PowerShell").GetString() != session.RuntimeVersion
                    || root.GetProperty("ProfileLoaded").GetBoolean())
                    throw new InvalidOperationException("Runtime version mismatch or an automatic profile was executed.");
                var basePath = Path.GetFullPath(AppContext.BaseDirectory);
                foreach (var module in root.GetProperty("Modules").EnumerateArray())
                    EnsureBundled(module.GetProperty("Path").GetString()!, basePath);
                var expectedModules = OperatingSystem.IsWindows() ? 5 : 3;
                if (root.GetProperty("Modules").GetArrayLength() != expectedModules)
                    throw new InvalidOperationException("Not all required SDK modules were imported.");
                foreach (var assembly in root.GetProperty("Assemblies").EnumerateArray())
                    EnsureBundled(assembly.GetString()!, basePath);
                Checks.Add(new("Runtime, Import-Module and Add-Type", script, true, root.GetRawText()));
            }
            finally { session.ReleaseResult(runtime.Id); }
            Checks.Add(new("SDK-only limitations", "Check for pwsh beside the embedded engine", true,
                "Start-Job is not a supported SDK-only capability; no external PowerShell is used by these probes. Automatic profiles remain disabled."));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            File.Delete(Path.Combine(scratch, "probe.txt"));
            Directory.Delete(scratch);
        }
    }

    private async Task CheckQueryAsync(PowerShellSession session, ConsoleNode node, Func<ConsoleResult, bool> predicate,
        string detail, CancellationToken cancellationToken)
    {
        var result = await session.QueryAsync(node, cancellationToken);
        try
        {
            if (node.Kind != ResourceKind.Processes || result.Outcome != InvocationOutcome.CompletedWithErrors)
                EnsureCompleted(result);
            if (!predicate(result)) throw new InvalidOperationException(detail);
            var summary = node.Kind == ResourceKind.Providers
                ? string.Join(", ", result.Rows.Select(row => row.Cells["Name"].Display))
                : node.Kind == ResourceKind.Drives
                    ? string.Join(", ", result.Rows.Select(row => $"{row.RelatedNode?.Path} ({row.RelatedNode?.ProviderName})"))
                    : $"{result.Rows.Count} rows; {detail}";
            if (result.Outcome == InvocationOutcome.CompletedWithErrors)
                summary += $"\nOutcome: {result.Outcome}; inaccessible processes remain explicit:\n" +
                    string.Join(Environment.NewLine, result.Diagnostics.Select(record => record.Message));
            Checks.Add(new(node.Name, result.Script, true, summary));
        }
        finally { session.ReleaseResult(result.Id); }
    }

    private static void EnsureCompleted(ConsoleResult result)
    {
        if (result.Outcome != InvocationOutcome.Completed)
            throw new InvalidOperationException($"{result.Script}: {result.Outcome}. " +
                string.Join(Environment.NewLine, result.Diagnostics.Select(record => record.Message)));
    }

    private static void EnsureBundled(string path, string basePath)
    {
        if (!Path.GetFullPath(path).StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException($"A module or assembly was loaded outside the published payload: {path}");
    }
}
