using System.Collections;

namespace Runspace.Core;

public static class ResultView
{
    public static Comparer<ConsoleRow> CreateComparer(string key) =>
        Comparer<ConsoleRow>.Create((left, right) => Compare(Value(left, key), Value(right, key)));

    public static IReadOnlyList<ConsoleRow> Apply(
        IEnumerable<ConsoleRow> rows, string? filter, string? sortKey = null, bool descending = false)
    {
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? rows
            : rows.Where(row => row.SearchText.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        if (sortKey is null)
            return filtered.ToArray();

        var comparer = Comparer<object?>.Create(Compare);
        return (descending
            ? filtered.OrderByDescending(row => Value(row, sortKey), comparer)
            : filtered.OrderBy(row => Value(row, sortKey), comparer)).ToArray();
    }

    private static object? Value(ConsoleRow row, string key) => row.Cells.TryGetValue(key, out var cell) ? cell.Value : null;

    private static int Compare(object? left, object? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        if (IsNumber(left) && IsNumber(right))
            return Convert.ToDecimal(left).CompareTo(Convert.ToDecimal(right));
        if (left.GetType() == right.GetType() && left is IComparable comparable)
            return comparable.CompareTo(right);
        return StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString());
    }

    private static bool IsNumber(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
