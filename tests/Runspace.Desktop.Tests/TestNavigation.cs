using Avalonia.Controls;
using Avalonia.VisualTree;
using Runspace.Core;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;

namespace Runspace.Desktop.Tests;

internal static class TestNavigation
{
    public static void ShowResource(Window window)
    {
        if (window is MainWindow main)
        {
            window.Opened += (_, _) =>
            {
                Assert.True(main.IsSessionReady);
                window.UpdateLayout();
                var model = (ConsoleViewModel)window.DataContext!;
                var tree = window.FindControl<TreeView>("NavigationTree")!;
                tree.GetVisualDescendants().OfType<TreeViewItem>()
                    .Single(item => item.DataContext == model.Roots[2]).IsExpanded = true;
                tree.SelectedItem = model.Roots[0].Children.Single(item => item.Node.Kind == ResourceKind.Processes);
            };
        }
        window.Show();
    }
}
