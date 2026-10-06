using System.Runtime.InteropServices;

namespace Runspace.PowerShell;

internal static class PowerShellPayload
{
    internal static IReadOnlyList<string> RequiredFiles(bool windows)
    {
        var modules = Path.Combine("runtimes", windows ? "win" : "unix", "lib", "net10.0", "Modules");
        List<string> files =
        [
            "System.Management.Automation.dll",
            "Microsoft.PowerShell.Commands.Management.dll",
            "Microsoft.PowerShell.Commands.Utility.dll",
            "Microsoft.PowerShell.Security.dll"
        ];
        foreach (var module in new[] { "Microsoft.PowerShell.Management", "Microsoft.PowerShell.Utility", "Microsoft.PowerShell.Security" })
            files.Add(Path.Combine(modules, module, module + ".psd1"));
        if (windows)
        {
            files.Add("Microsoft.Management.Infrastructure.CimCmdlets.dll");
            files.Add("Microsoft.PowerShell.Commands.Diagnostics.dll");
            files.Add("microsoft.management.infrastructure.native.dll");
            files.Add("microsoft.management.infrastructure.native.unmanaged.dll");
            foreach (var module in new[] { "CimCmdlets", "Microsoft.PowerShell.Diagnostics" })
                files.Add(Path.Combine(modules, module, module + ".psd1"));
        }
        else
            files.Add(OperatingSystem.IsMacOS() ? "libpsl-native.dylib" : "libpsl-native.so");
        return files;
    }

    internal static void Validate(string baseDirectory, bool windows)
    {
        var missing = RequiredFiles(windows).Where(file =>
            !File.Exists(Path.Combine(baseDirectory, file))
            && !File.Exists(Path.Combine(baseDirectory, "runtimes", windows ? "win" : "unix", "lib", "net10.0", file))
            && !File.Exists(Path.Combine(baseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "lib", "netstandard1.6", file))
            && !File.Exists(Path.Combine(baseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"The embedded PowerShell payload is incomplete in '{baseDirectory}'. Missing: {string.Join(", ", missing)}. " +
                "Extract the entire matching runspace-<rid> artifact again, including the runtimes directory. " +
                "Installing PowerShell or changing PSModulePath does not repair this application payload.");
    }
}
