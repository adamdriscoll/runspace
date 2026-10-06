using System.Management.Automation;
using Runspace.Core;
using AutomationShell = System.Management.Automation.PowerShell;

namespace Runspace.PowerShell;

internal sealed record QueryDefinition(
    IReadOnlyList<ConsoleColumn> Columns, string Description, Action<AutomationShell> Configure)
{
    public static QueryDefinition For(ConsoleNode node) => node.Kind switch
    {
        ResourceKind.Processes => Command("Get-Process", ProcessColumns(ResourceKind.Processes)),
        ResourceKind.ProcessModules or ResourceKind.ProcessThreads => ProcessView(node),
        ResourceKind.Services => Command("Get-Service",
            [Text("Name"), Text("DisplayName", 240), Text("Status"), Text("StartType")]),
        ResourceKind.EventLogs => Command("Get-WinEvent",
            [Text("LogName", 320), Number("RecordCount"), Text("LogMode"), Boolean("IsEnabled")],
            ("ListLog", "*")),
        ResourceKind.EventEntries => Command("Get-WinEvent",
            [Date("TimeCreated"), Number("Id"), Text("LevelDisplayName"), Text("ProviderName", 240), Text("Message", 450)],
            ("LogName", Path(node)), ("MaxEvents", 200)),
        ResourceKind.NetworkProperties => Native("[System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()",
            OperatingSystem.IsWindows()
                ? [Text("HostName"), Text("DomainName"), Text("DhcpScopeName"), Boolean("IsWinsProxy"), Text("NodeType")]
                : [Text("HostName"), Text("DomainName"), Text("NodeType")]),
        ResourceKind.NetworkInterfaces => Native("[System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()",
            [Text("Name", 240), Text("Description", 320), Text("NetworkInterfaceType"), Text("OperationalStatus"),
                Number("Speed"), Text("MAC"), Text("Addresses", 350)]),
        ResourceKind.ManagedComputers => Native("[pscustomobject]@{ Name = [Environment]::MachineName; OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription; Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() }",
            [Text("Name", 240), Text("OS", 400), Text("Architecture")]),
        ResourceKind.Drives => Command("Get-PSDrive",
            [Text("Name"), Text("Provider"), Text("Root", 320), Bytes("Used"), Bytes("Free")]),
        ResourceKind.Providers => Command("Get-PSProvider",
            [Text("Name"), Text("Capabilities", 360), Text("Drives", 240)]),
        ResourceKind.ProviderPath => Command("Get-ChildItem",
            ProviderColumns(node.ProviderName), ("LiteralPath", Path(node))),
        ResourceKind.Registry => Native("[pscustomobject]@{ Name = 'HKEY_LOCAL_MACHINE'; Path = 'HKLM:\\' }; [pscustomobject]@{ Name = 'HKEY_CURRENT_USER'; Path = 'HKCU:\\' }", [Text("Name", 260), Text("Path")]),
        ResourceKind.Shares => Command("Get-CimInstance",
            [Text("Name"), Text("Path", 350), Text("Description", 320), Number("Type")], ("ClassName", "Win32_Share")),
        ResourceKind.LocalUsers => Script(LocalUsersScript,
            [Text("Name"), Text("FullName"), Text("Description", 320), Boolean("Enabled"), Text("SID")]),
        ResourceKind.LocalGroups => Script(LocalGroupsScript,
            [Text("Name"), Text("Description", 320), Text("SID")]),
        ResourceKind.GroupMembers => Script(GroupMembersScript,
            [Text("Name", 260), Text("ObjectClass"), Text("PrincipalSource"), Text("SID")], ("Group", Path(node))),
        ResourceKind.WmiNamespaces => Script(NamespaceScript,
            [Text("Name"), Text("EntryType"), Text("Namespace", 350)], ("Namespace", node.Path ?? "root")),
        ResourceKind.WmiClasses => Command("Get-CimClass",
            [Text("CimClassName", 350), Text("CimSuperClassName", 300), Text("CimClassProperties", 450)],
            ("Namespace", Path(node))),
        ResourceKind.WmiInstances => Instances(node),
        ResourceKind.Environment => Command("Get-ChildItem", [Text("Name", 240), Text("Value", 600)], ("LiteralPath", "Env:\\")),
        ResourceKind.Overview => throw new ArgumentException("Overview is a desktop view. Select a built-in resource node.", nameof(node)),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node.Kind, "Unsupported resource kind.")
    };

    internal static IReadOnlyList<ConsoleColumn> ProcessColumns(ResourceKind kind) => kind switch
    {
        ResourceKind.Processes => [Text("Name"), Number("Id"), Number("CPU"), Bytes("WorkingSet"), Number("Threads"), Number("Handles")],
        ResourceKind.ProcessModules => [Text("ModuleName", 260), Text("FileName", 550), Bytes("ModuleMemorySize"), Text("BaseAddress"), Text("FileVersionInfo", 450)],
        ResourceKind.ProcessThreads => [Number("Id"), Text("ThreadState"), Text("WaitReason"), Date("StartTime"),
            Text("TotalProcessorTime"), Text("PriorityLevel"), Number("BasePriority"), Number("CurrentPriority")],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static QueryDefinition ProcessView(ConsoleNode node)
    {
        var property = node.Kind == ResourceKind.ProcessModules ? "Modules" : "Threads";
        var script = "param([int] $ProcessId) Get-Process -Id $ProcessId -ErrorAction Stop | ForEach-Object -ErrorAction Stop { $_." + property + " }";
        var describedId = int.TryParse(node.Path, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? PowerShellDisplay.Literal(parsed) : PowerShellDisplay.Literal(node.Path);
        return new QueryDefinition(ProcessColumns(node.Kind),
            $"Get-Process -Id {describedId} -ErrorAction 'Stop' | ForEach-Object -ErrorAction 'Stop' {{ $_.{property} }}", shell =>
        {
            if (!int.TryParse(node.Path, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var processId))
                throw new ArgumentException("A process related-view node requires a non-negative numeric process Id.");
            shell.AddScript(script, useLocalScope: true).AddParameter("ProcessId", processId);
        });
    }

    private static QueryDefinition Instances(ConsoleNode node)
    {
        var parts = Path(node).Split('|', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException("A CIM instance node must contain namespace|class in its Path.", nameof(node));
        return Command("Get-CimInstance",
            [Text("CimClass"), Text("CimInstanceProperties", 700), Text("CimSystemProperties", 400)],
            ("Namespace", parts[0]), ("ClassName", parts[1]));
    }

    private static IReadOnlyList<ConsoleColumn> ProviderColumns(string? provider) => provider switch
    {
        "Registry" => [Text("Name", 340), Text("Property", 600)],
        "Environment" => [Text("Name", 240), Text("Value", 600)],
        "Variable" => [Text("Name", 240), Text("Value", 600), Text("Options")],
        "Alias" => [Text("Name", 240), Text("Definition", 600), Text("Options")],
        "Function" => [Text("Name", 240), Text("Definition", 600)],
        "Certificate" => [Text("Name", 260), Text("Subject", 450), Text("Thumbprint", 320), Date("NotBefore"), Date("NotAfter"), Boolean("HasPrivateKey")],
        _ => [Text("Name", 300), Boolean("PSIsContainer"), Bytes("Length"), Date("LastWriteTime"), Text("Mode")]
    };

    private static QueryDefinition Command(string command, IReadOnlyList<ConsoleColumn> columns,
        params (string Name, object Value)[] parameters) =>
        new(columns, command + PowerShellDisplay.Arguments(parameters.Select(parameter => (parameter.Name, (object?)parameter.Value))), shell =>
        {
            shell.AddCommand(command);
            foreach (var parameter in parameters)
                shell.AddParameter(parameter.Name, parameter.Value);
        });

    private static QueryDefinition Script(string script, IReadOnlyList<ConsoleColumn> columns,
        params (string Name, object Value)[] parameters) =>
        new(columns, "& {" + Environment.NewLine + script + Environment.NewLine + "}"
            + PowerShellDisplay.Arguments(parameters.Select(parameter => (parameter.Name, (object?)parameter.Value))), shell =>
        {
            shell.AddScript(script, useLocalScope: true);
            foreach (var parameter in parameters)
                shell.AddParameter(parameter.Name, parameter.Value);
        });

    private static QueryDefinition Native(string description, IReadOnlyList<ConsoleColumn> columns) =>
        new(columns, description, _ => { });
    private static string Path(ConsoleNode node) => !string.IsNullOrEmpty(node.Path)
        ? node.Path : throw new ArgumentException("This node requires a provider path or resource identity.", nameof(node));
    private static ConsoleColumn Text(string key, double width = 150) => new(key, key, Width: width);
    private static ConsoleColumn Number(string key) => new(key, key, ColumnKind.Number);
    private static ConsoleColumn Bytes(string key) => new(key, key, ColumnKind.Bytes);
    private static ConsoleColumn Boolean(string key) => new(key, key, ColumnKind.Boolean);
    private static ConsoleColumn Date(string key) => new(key, key, ColumnKind.DateTime, 180);

    private const string LocalUsersScript = """
        if ($ExecutionContext.InvokeCommand.GetCommand('Get-LocalUser', [System.Management.Automation.CommandTypes]::Cmdlet)) {
            Get-LocalUser
        } else {
            try {
                Write-Information 'LocalAccounts is unavailable; using the read-only WinNT ADSI user provider.' -InformationAction Continue
                $computer = [ADSI]('WinNT://' + [Environment]::MachineName + ',computer')
                foreach ($child in $computer.Children) {
                    if ($child.SchemaClassName -eq 'User') {
                        $child | Add-Member -NotePropertyName Name -NotePropertyValue ([string]$child.psbase.Name) -Force
                        $child | Add-Member -NotePropertyName FullName -NotePropertyValue ([string]$child.Properties['FullName'].Value) -Force
                        $child | Add-Member -NotePropertyName Description -NotePropertyValue ([string]$child.Properties['Description'].Value) -Force
                        $child | Add-Member -NotePropertyName SID -NotePropertyValue ([System.Security.Principal.SecurityIdentifier]::new([byte[]]$child.Properties['objectSid'].Value, 0)) -Force
                        $child | Add-Member -NotePropertyName Enabled -NotePropertyValue (-not ([int]$child.Properties['UserFlags'].Value -band 2)) -PassThru -Force
                    }
                }
            } catch {
                throw "LocalAccounts module unavailable and WinNT ADSI user enumeration failed: $($_.Exception.Message)"
            }
        }
        """;

    private const string LocalGroupsScript = """
        if ($ExecutionContext.InvokeCommand.GetCommand('Get-LocalGroup', [System.Management.Automation.CommandTypes]::Cmdlet)) {
            Get-LocalGroup
        } else {
            try {
                Write-Information 'LocalAccounts is unavailable; using the read-only WinNT ADSI group provider.' -InformationAction Continue
                $computer = [ADSI]('WinNT://' + [Environment]::MachineName + ',computer')
                foreach ($child in $computer.Children) {
                    if ($child.SchemaClassName -eq 'Group') {
                        $child | Add-Member -NotePropertyName Name -NotePropertyValue ([string]$child.psbase.Name) -Force
                        $child | Add-Member -NotePropertyName Description -NotePropertyValue ([string]$child.Properties['Description'].Value) -Force
                        $child | Add-Member -NotePropertyName SID -NotePropertyValue ([System.Security.Principal.SecurityIdentifier]::new([byte[]]$child.Properties['objectSid'].Value, 0)) -PassThru -Force
                    }
                }
            } catch {
                throw "LocalAccounts module unavailable and WinNT ADSI group enumeration failed: $($_.Exception.Message)"
            }
        }
        """;

    private const string GroupMembersScript = """
        param([string] $Group)
        if ($ExecutionContext.InvokeCommand.GetCommand('Get-LocalGroupMember', [System.Management.Automation.CommandTypes]::Cmdlet)) {
            Get-LocalGroupMember -Group $Group
        } else {
            try {
                Write-Information 'LocalAccounts is unavailable; using the read-only WinNT ADSI membership provider.' -InformationAction Continue
                $computer = [ADSI]('WinNT://' + [Environment]::MachineName + ',computer')
                $groupObject = $null
                foreach ($child in $computer.Children) {
                    if ($child.SchemaClassName -eq 'Group' -and $child.Name -eq $Group) {
                        $groupObject = $child
                        break
                    }
                }
                if ($null -eq $groupObject) { throw 'The local group no longer exists.' }
                foreach ($member in $groupObject.psbase.Invoke('Members')) {
                    [System.DirectoryServices.DirectoryEntry]::new($member)
                }
            } catch {
                throw "LocalAccounts module unavailable and WinNT ADSI membership enumeration failed: $($_.Exception.Message)"
            }
        }
        """;

    private const string NamespaceScript = """
        param([string] $Namespace)
        Get-CimInstance -Namespace $Namespace -ClassName __Namespace -ErrorAction Stop | ForEach-Object {
            $_ | Add-Member -NotePropertyName EntryType -NotePropertyValue Namespace -PassThru |
                Add-Member -NotePropertyName Namespace -NotePropertyValue ($Namespace + '\' + $_.Name) -PassThru
        }
        [pscustomobject]@{ Name = 'Classes'; EntryType = 'Classes'; Namespace = $Namespace }
        """;
}
