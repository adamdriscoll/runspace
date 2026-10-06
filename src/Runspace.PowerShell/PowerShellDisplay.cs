using System.Globalization;

namespace Runspace.PowerShell;

// History representations only: callers must continue binding parameters through the SDK.
internal static class PowerShellDisplay
{
    public static string Literal(object? value) => value switch
    {
        null => "$null",
        bool boolean => boolean ? "$true" : "$false",
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => "'" + System.Management.Automation.Language.CodeGeneration.EscapeSingleQuotedStringContent(
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty) + "'"
    };

    public static string Arguments(IEnumerable<(string Name, object? Value)> parameters) =>
        string.Concat(parameters.Select(parameter => $" -{parameter.Name} {Literal(parameter.Value)}"));

    public static string Command(string command, params (string Name, object? Value)[] parameters) =>
        command + Arguments(parameters);
}
