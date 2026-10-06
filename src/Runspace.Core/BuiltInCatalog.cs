namespace Runspace.Core;

public static class BuiltInCatalog
{
    public static IReadOnlyList<ConsoleNode> LocalSystem { get; } =
    [
        new("processes", "Processes", ResourceKind.Processes, "Inspect running processes and their resource usage."),
        new("services", "Services", ResourceKind.Services, "Inspect, start, stop, and restart Windows services.", WindowsOnly: true),
        new("event-logs", "Event Logs", ResourceKind.EventLogs, "Browse Windows event logs and their recent entries.", WindowsOnly: true),
        new("network-properties", "IP Properties", ResourceKind.NetworkProperties, "Host name, domain, and IP configuration."),
        new("network-interfaces", "Network Interfaces", ResourceKind.NetworkInterfaces, "Network adapters, addresses, and connection status."),
        new("registry", "Registry", ResourceKind.Registry, "Browse registry keys and values through the PowerShell provider.", WindowsOnly: true),
        new("drives", "Drives", ResourceKind.Drives, "PowerShell provider drives, not just physical disks."),
        new("shares", "Shares", ResourceKind.Shares, "Local Windows shared folders.", WindowsOnly: true),
        new("local-users", "Users", ResourceKind.LocalUsers, "Local Windows user accounts.", WindowsOnly: true),
        new("local-groups", "Groups", ResourceKind.LocalGroups, "Local Windows groups and their members.", WindowsOnly: true),
        new("wmi", "WMI Browser", ResourceKind.WmiNamespaces, "Browse CIM namespaces, classes, and instances.", "root", true),
        new("environment", "Environment", ResourceKind.Environment, "Inspect and edit environment variables in this PowerShell session."),
        new("providers", "PowerShell Providers", ResourceKind.Providers, "Discover every provider available in the embedded session.")
    ];

    public static IReadOnlyList<ConsoleAction> GetActions(ConsoleNode node, IReadOnlyList<ConsoleRow> selection)
    {
        var count = selection.Count;
        var single = count == 1;
        var actions = new List<ConsoleAction>
        {
            new(ConsoleActionId.Properties, "Properties...", "General", "Inspect the original object's properties.", single),
            new(ConsoleActionId.Copy, "Copy to clipboard", "General", "Copy selected rows with column headings.", count > 0),
            new(ConsoleActionId.Export, "Export table...", "Export", "Save the visible table as CSV.", true)
        };

        if (selection.Any(row => row.RelatedNode is not null))
            actions.Add(new(ConsoleActionId.Browse, "Open related view", "Related information",
                "Browse children, event entries, group members, or class instances.", single && selection[0].RelatedNode is not null));

        switch (node.Kind)
        {
            case ResourceKind.Processes:
                actions.Add(new(ConsoleActionId.StartProcess, "Start process...", "General", "Start an executable with your current account's permissions.", true,
                    Parameters: [new("FilePath", "Executable"), new("Arguments", "Arguments", false)]));
                actions.Add(new(ConsoleActionId.StopProcess, "Stop process...", "General", "Stop the selected processes.", count > 0, true));
                actions.Add(new(ConsoleActionId.SetProcessPriority, "Set priority class...", "General", "Set the selected process's priority (Windows).", single && OperatingSystem.IsWindows(), true,
                    [new("Priority", "Priority (Idle, BelowNormal, Normal, AboveNormal, High)", DefaultValue: "Normal")]));
                actions.Add(new(ConsoleActionId.ProcessModules, "DLLs / modules", "Related information", "Inspect the selected process's loaded modules.", single));
                actions.Add(new(ConsoleActionId.ProcessThreads, "Threads", "Related information", "Inspect the selected process's threads.", single));
                break;
            case ResourceKind.Services:
                actions.Add(new(ConsoleActionId.StartService, "Start service", "Service", "Start the selected services.", count > 0, true));
                actions.Add(new(ConsoleActionId.StopService, "Stop service...", "Service", "Stop the selected services.", count > 0, true));
                actions.Add(new(ConsoleActionId.RestartService, "Restart service...", "Service", "Restart the selected services.", count > 0, true));
                break;
            case ResourceKind.Drives:
                actions.Add(new(ConsoleActionId.AddDrive, "Add drive...", "Drive", "Add a provider drive to this session.", true,
                    Parameters: [new("Name", "Drive name"), new("Provider", "Provider", DefaultValue: "FileSystem"), new("Root", "Root")]));
                actions.Add(new(ConsoleActionId.RemoveDrive, "Remove drive...", "Drive",
                    "Remove selected session drives. This does not delete their underlying resources.", count > 0, true));
                break;
            case ResourceKind.Environment:
                actions.Add(new(ConsoleActionId.SetValue, "Edit value...", "Environment", "Change the variable in this session.", single,
                    Parameters: [new("Value", "Value", false)]));
                actions.Add(new(ConsoleActionId.RemoveItem, "Remove variable...", "Environment", "Remove this session's variable.", count > 0, true));
                break;
        }

        if (node.WindowsOnly && !OperatingSystem.IsWindows())
            return actions.Select(action => action with { IsEnabled = false }).ToArray();
        return actions;
    }
}
