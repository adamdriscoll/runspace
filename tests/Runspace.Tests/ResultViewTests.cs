using Runspace.Core;

namespace Runspace.Tests;

public sealed class ResultViewTests
{
    private static ConsoleRow Row(object? number, string name) => new(Guid.NewGuid(), new Dictionary<string, ConsoleCell>
    {
        ["Id"] = ConsoleCell.From(number),
        ["Name"] = ConsoleCell.From(name)
    });

    [Fact]
    public void SortingPreservesNumericTypesAndNulls()
    {
        var rows = new[] { Row(100, "large"), Row(2, "small"), Row(null, "empty"), Row(10L, "medium") };
        Assert.Equal(["empty", "small", "medium", "large"], ResultView.Apply(rows, null, "Id").Select(row => row.Cells["Name"].Display));
        Assert.Equal(["large", "medium", "small", "empty"], ResultView.Apply(rows, null, "Id", true).Select(row => row.Cells["Name"].Display));
    }

    [Fact]
    public void FilteringIsCaseInsensitiveAndDoesNotMutateSource()
    {
        var rows = new[] { Row(1, "PowerShell"), Row(2, "Other") };
        Assert.Single(ResultView.Apply(rows, "powershell"));
        Assert.Equal(2, rows.Length);
        Assert.Empty(ResultView.Apply(rows, "missing"));
    }

    [Fact]
    public void CachedDisplayCanFormatBytesWithoutLosingSortValue()
    {
        var cell = ConsoleCell.From(2048L, ColumnKind.Bytes);
        Assert.Equal(2048L, cell.Value);
        Assert.Contains("KB", cell.Display);
        Assert.Equal("(null)", ConsoleCell.From(null).Display);
    }

    [Fact]
    public void ActionsRequireDeliberateSelectionAndConfirmMutations()
    {
        var node = BuiltInCatalog.LocalSystem.Single(node => node.Kind == ResourceKind.Processes);
        var empty = BuiltInCatalog.GetActions(node, []);
        Assert.False(empty.Single(action => action.Id == ConsoleActionId.StopProcess).IsEnabled);
        var selected = BuiltInCatalog.GetActions(node, [Row(1, "fixture")]);
        Assert.True(selected.Single(action => action.Id == ConsoleActionId.Properties).IsEnabled);
        Assert.True(selected.Single(action => action.Id == ConsoleActionId.StopProcess).RequiresConfirmation);
        var multiple = BuiltInCatalog.GetActions(node, [Row(1, "one"), Row(2, "two")]);
        Assert.False(multiple.Single(action => action.Id == ConsoleActionId.Properties).IsEnabled);
        Assert.False(multiple.Single(action => action.Id == ConsoleActionId.ProcessModules).IsEnabled);
    }

    [Fact]
    public void RelatedViewIsNotAvailableForUnrelatedRows()
    {
        var node = BuiltInCatalog.LocalSystem.Single(node => node.Kind == ResourceKind.Drives);
        Assert.DoesNotContain(BuiltInCatalog.GetActions(node, [Row(1, "one")]), action => action.Id == ConsoleActionId.Browse);
        var row = Row(1, "folder") with { RelatedNode = new("child", "folder", ResourceKind.ProviderPath, "Browse.", "Env:") };
        Assert.True(BuiltInCatalog.GetActions(node, [row]).Single(action => action.Id == ConsoleActionId.Browse).IsEnabled);
    }
}
