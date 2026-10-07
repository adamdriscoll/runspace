using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Runspace.Core;
using Runspace.Desktop;
using Runspace.Desktop.ViewModels;

namespace Runspace.Desktop.Tests;

public sealed partial class ConsoleTests
{
    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var symbol = key is >= Key.A and <= Key.Z ? key.ToString().ToLowerInvariant() : null;
        window.KeyPress(key, modifiers, PhysicalKey.None, symbol);
        if (window.IsVisible)
        {
            window.KeyRelease(key, modifiers, PhysicalKey.None, symbol);
            window.UpdateLayout();
        }
    }

    private static ConsoleResult DenseFixture()
    {
        ConsoleColumn[] columns = [new("Name", "Name", Width: 180), new("Id", "Id", ColumnKind.Number),
            new("Value", "Value", Width: 220)];
        var rows = Enumerable.Range(1, 1000).Select(index => new ConsoleRow(
            new Guid(index, 0, 0, new byte[8]), new Dictionary<string, ConsoleCell>
            {
                ["Name"] = ConsoleCell.From($"fixture-{index:0000}"),
                ["Id"] = ConsoleCell.From(index, ColumnKind.Number),
                ["Value"] = ConsoleCell.From(index % 2 == 0 ? (object)(index * 10) : "Benign sample")
            })).ToArray();
        return new(new Guid("00000000-0000-0000-0000-000000000001"), columns, rows,
            "# Original fixture objects; no script execution", [], TimeSpan.Zero, InvocationOutcome.Completed);
    }

    private static void Capture(Window window, string name)
    {
        using var image = window.CaptureRenderedFrame();
        Assert.NotNull(image);
        Assert.Equal(new PixelSize((int)Math.Round(window.ClientSize.Width * window.RenderScaling),
            (int)Math.Round(window.ClientSize.Height * window.RenderScaling)), image.PixelSize);
        if (Environment.GetEnvironmentVariable("RUNSPACE_UI_CAPTURE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            image.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
        }
    }

    private static void AssertFits(Control control, Visual container)
    {
        var origin = control.TranslatePoint(default, container);
        Assert.NotNull(origin);
        Assert.True(origin.Value.X >= -1 && origin.Value.Y >= -1 &&
            origin.Value.X + control.Bounds.Width <= container.Bounds.Width + 1 &&
            origin.Value.Y + control.Bounds.Height <= container.Bounds.Height + 1,
            $"{control.Name ?? control.GetType().Name}: {origin}, {control.Bounds.Size} outside {container.Bounds.Size}");
    }

    [AvaloniaTheory]
    [InlineData(1000, 680, 1)]
    [InlineData(1200, 800, 1)]
    [InlineData(1000, 680, 1.5)]
    [InlineData(1200, 800, 1.5)]
    [InlineData(1000, 680, 2)]
    [InlineData(1200, 800, 2)]
    public async Task CompactClientSizesAndDpiKeepTwentyRowsAndVirtualize(int width, int height, double scale)
    {
        var result = DenseFixture();
        var session = new FixtureSession { Query = _ => Task.FromResult(result) };
        var window = new MainWindow(session) { Width = width, Height = height };
        TestNavigation.ShowResource(window);
        try
        {
            window.SetRenderScaling(scale);
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.UpdateLayout();
            Assert.Equal(new Size(width, height), window.ClientSize);
            Assert.Equal(scale, window.RenderScaling);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItem = result.Rows[0];
            window.UpdateLayout();
            Capture(window, $"console-{width}x{height}-{scale * 100:0}");
            var presenter = grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().Single();
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().ToArray();
            var complete = rows.Count(row => row.TranslatePoint(default, presenter) is { } origin &&
                origin.Y >= 0 && origin.Y + row.Bounds.Height <= presenter.Bounds.Height &&
                row.TranslatePoint(default, grid) is { } gridOrigin &&
                gridOrigin.Y >= grid.ColumnHeaderHeight && gridOrigin.Y + row.Bounds.Height <= grid.Bounds.Height);
            Assert.True(complete >= 20, $"Only {complete} complete rows at {width} x {height}, {scale * 100}%.");
            Assert.InRange(rows.Length, 20, 60);
            if (Environment.GetEnvironmentVariable("RUNSPACE_UI_CAPTURE_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"console-{width}x{height}-{scale * 100:0}.json"),
                    JsonSerializer.Serialize(new { ClientWidth = width, ClientHeight = height, RenderScaling = scale,
                        CompleteRows = complete, RealizedRows = rows.Length, TotalRows = result.Rows.Count },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            Assert.Equal(1000, grid.ItemsSource!.Cast<ConsoleRow>().Count());
            Assert.False(window.FindControl<Border>("DiagnosticsPane")!.IsVisible);
            foreach (var control in window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
                AssertFits(control, window);
            foreach (var name in new[] { "FilterBox", "NavigationSplitter", "ActionsSplitter", "ColumnsButton" })
                AssertFits(window.FindControl<Control>(name)!, window);
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
                foreach (var text in button.GetVisualDescendants().OfType<TextBlock>())
                    AssertFits(text, button);
            grid.ScrollIntoView(result.Rows[^1], grid.Columns[0]);
            window.UpdateLayout();
            Assert.Contains(grid.GetVisualDescendants().OfType<DataGridRow>(), row => ReferenceEquals(row.DataContext, result.Rows[^1]));
            Assert.Equal(1, session.Queries);
            Assert.Null(session.LastSelection);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task KeyboardContextMenuAndPaneAgreeWithoutExecutingOnSelection(int count)
    {
        var session = new FixtureSession();
        var window = new MainWindow(session);
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.UpdateLayout();
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItems.Clear();
            grid.SelectedItem = null;
            foreach (var row in grid.ItemsSource!.Cast<ConsoleRow>().Take(count)) grid.SelectedItems.Add(row);
            Assert.Equal(count, grid.SelectedItems.Count);
            grid.Focus();
            Press(window, Key.F10, RawInputModifiers.Shift);
            var menu = window.FindControl<ContextMenu>("ResultContextMenu")!;
            Assert.True(menu.IsOpen);
            var pane = window.FindControl<StackPanel>("ActionsPanel")!.Children.OfType<Button>()
                .ToDictionary(button => button.Tag!, button => button.IsEnabled);
            Assert.Equal(pane.Count, menu.ItemsSource!.Cast<MenuItem>().Count());
            foreach (var item in menu.ItemsSource!.Cast<MenuItem>()) Assert.Equal(pane[item.Tag!], item.IsEnabled);
            var propertiesButton = window.FindControl<StackPanel>("ActionsPanel")!.Children.OfType<Button>()
                .Single(button => Equals(button.Tag, ConsoleActionId.Properties));
            Assert.Equal(count == 1, ControlAutomationPeer.CreatePeerForElement(propertiesButton)!.IsEnabled());
            if (count != 1) Assert.Contains("Unavailable", ControlAutomationPeer.CreatePeerForElement(propertiesButton)!.GetHelpText());
            Assert.Equal(count == 1, pane[ConsoleActionId.Properties]);
            Assert.Equal(count > 0, pane[ConsoleActionId.StopProcess]);
            Assert.Equal(1, session.Queries);
            Assert.Null(session.LastSelection);
            menu.Close();
            grid.Focus();
            Press(window, Key.Apps);
            Assert.True(menu.IsOpen);
            menu.Close();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ContextMenuPropertiesCanBeInvokedWithOnlyKeys()
    {
        var window = new MainWindow(new FixtureSession());
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.UpdateLayout();
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
            grid.Focus();
            Press(window, Key.F10, RawInputModifiers.Shift);
            Press(window, Key.Down);
            Press(window, Key.Enter);
            await UntilAsync(() => window.OwnedWindows.Any());
            Press(window.OwnedWindows.Single(), Key.Escape);
            await UntilAsync(() => !window.OwnedWindows.Any());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RightClickKeepsSelectedRowsAndSelectsAnUnselectedRowBeforeResolvingActions()
    {
        var result = DenseFixture() with { Rows = DenseFixture().Rows.Take(3).ToArray() };
        var session = new FixtureSession { Query = _ => Task.FromResult(result) };
        var window = new MainWindow(session);
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.UpdateLayout();
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItems.Add(result.Rows[0]);
            grid.SelectedItems.Add(result.Rows[1]);
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().OrderBy(row => row.Index).ToArray();
            void RightClick(DataGridRow row)
            {
                var point = row.TranslatePoint(new Point(20, row.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Right);
                window.MouseUp(point, MouseButton.Right);
            }
            RightClick(rows[0]);
            Assert.Equal(2, grid.SelectedItems.Count);
            var menu = window.FindControl<ContextMenu>("ResultContextMenu")!;
            Assert.True(menu.IsOpen);
            Assert.False(menu.ItemsSource!.Cast<MenuItem>().Single(item => Equals(item.Tag, ConsoleActionId.Properties)).IsEnabled);
            menu.Close();
            RightClick(rows[2]);
            Assert.Same(result.Rows[2], Assert.Single(grid.SelectedItems.Cast<ConsoleRow>()));
            Assert.True(menu.ItemsSource!.Cast<MenuItem>().Single(item => Equals(item.Tag, ConsoleActionId.Properties)).IsEnabled);
            Assert.Equal(1, session.Queries);
            Assert.Null(session.LastSelection);
            menu.Close();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DiagnosticFailureKeepsKeyboardFocusAndHasNonColorStatus()
    {
        var session = new FixtureSession { Query = _ => Task.FromResult(DenseFixture() with
        {
            Outcome = InvocationOutcome.CompletedWithErrors,
            Diagnostics = [new(DateTimeOffset.UnixEpoch, "Error", "Benign fixture failure")]
        }) };
        var window = new MainWindow(session);
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.FindControl<TextBox>("FilterBox")!.Focus(NavigationMethod.Tab);
            Press(window, Key.F5);
            await UntilAsync(() => session.Queries == 2 && !((ConsoleViewModel)window.DataContext!).IsBusy);
            Assert.True(window.FindControl<TextBox>("FilterBox")!.IsFocused);
            Assert.True(window.FindControl<Border>("DiagnosticsPane")!.IsVisible);
            Assert.Contains("CompletedWithErrors", ((ConsoleViewModel)window.DataContext!).Status);
            Assert.Contains("error", window.FindControl<TextBlock>("ResultMessageText")!.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(AutomationLiveSetting.Polite,
                ControlAutomationPeer.CreatePeerForElement(window.FindControl<TextBlock>("ResultMessageText")!)!.GetLiveSetting());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MixedCachedValuesAndRelatedRowsKeepSafeEligibilityAndVisibleFilterReconciliation()
    {
        var related = new ConsoleNode("fixture-related", "Related fixture", ResourceKind.ProviderPath, "Benign related view", "Fixture:");
        var result = DenseFixture() with { Rows = DenseFixture().Rows.Take(3).Select((row, index) =>
            index == 0 ? row with { RelatedNode = related } : row).ToArray() };
        var session = new FixtureSession { Query = _ => Task.FromResult(result) };
        var window = new MainWindow(session);
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            window.UpdateLayout();
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItems.Add(result.Rows[0]);
            grid.SelectedItems.Add(result.Rows[1]);
            Assert.Equal(2, grid.SelectedItems.Count);
            var actions = window.FindControl<StackPanel>("ActionsPanel")!;
            Assert.False(actions.Children.OfType<Button>().Single(button => Equals(button.Tag, ConsoleActionId.Browse)).IsEnabled);
            window.FindControl<TextBox>("FilterBox")!.Text = "fixture-0001";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Same(result.Rows[0], Assert.Single(grid.SelectedItems.Cast<ConsoleRow>()));
            Assert.Contains("hidden selection cleared", ((ConsoleViewModel)window.DataContext!).Status);
            Assert.True(actions.Children.OfType<Button>().Single(button => Equals(button.Tag, ConsoleActionId.Browse)).IsEnabled);
            Assert.Equal(1, session.Queries);
            Assert.Null(session.LastSelection);
            grid.Focus();
            Press(window, Key.Enter);
            await UntilAsync(() => session.Queries == 2 && !((ConsoleViewModel)window.DataContext!).IsBusy);
            Press(window, Key.Left, RawInputModifiers.Alt);
            await UntilAsync(() => session.Queries == 3 && !((ConsoleViewModel)window.DataContext!).IsBusy);
            Assert.Contains("Processes", ((ConsoleViewModel)window.DataContext!).Title);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task KeyboardPropertiesFilterSelectAllAndSplitterRoutesAreReachable()
    {
        var session = new FixtureSession();
        var window = new MainWindow(session);
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.Focus();
            Press(window, Key.Down);
            Assert.Single(grid.SelectedItems);
            Press(window, Key.Enter);
            await UntilAsync(() => window.OwnedWindows.Any());
            var properties = window.OwnedWindows.Single();
            properties.UpdateLayout();
            Assert.True(Named<DataGrid>(properties, "PropertiesGrid").IsFocused);
            Press(properties, Key.Down);
            Assert.Contains("fixture", Named<TextBox>(properties, "PropertyValue").Text);
            Press(properties, Key.Escape);
            await UntilAsync(() => !window.OwnedWindows.Any());
            Press(window, Key.F, RawInputModifiers.Control);
            Assert.True(window.FindControl<TextBox>("FilterBox")!.IsFocused);
            grid.Focus();
            Press(window, Key.A, RawInputModifiers.Control);
            Assert.Equal(2, grid.SelectedItems.Count);
            var workspace = window.FindControl<Grid>("WorkspaceGrid")!;
            var splitter = window.FindControl<GridSplitter>("NavigationSplitter")!;
            splitter.Focus(NavigationMethod.Tab);
            var before = workspace.ColumnDefinitions[0].ActualWidth;
            Press(window, Key.Right);
            Assert.True(workspace.ColumnDefinitions[0].ActualWidth > before);
            for (var index = 0; index < 40; index++) Press(window, Key.Left);
            Assert.Equal(160, workspace.ColumnDefinitions[0].ActualWidth);
            Assert.Equal("Navigation pane width", ControlAutomationPeer.CreatePeerForElement(splitter)!.GetName());
            Assert.Contains(":focus-visible", splitter.Classes);
            Press(window, Key.Tab);
            Assert.False(splitter.IsFocused);
            window.FindControl<GridSplitter>("ActionsSplitter")!.Focus(NavigationMethod.Tab);
            for (var index = 0; index < 40; index++) Press(window, Key.Right);
            Assert.Equal(180, workspace.ColumnDefinitions[4].ActualWidth);
            foreach (var button in window.FindControl<StackPanel>("ActionsPanel")!.Children.OfType<Button>())
                foreach (var text in button.GetVisualDescendants().OfType<TextBlock>()) AssertFits(text, button);
            Assert.Null(session.LastSelection);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task KeyboardColumnControlsMoveResizeSortHideAndRestore()
    {
        var window = new MainWindow(new FixtureSession());
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            window.FindControl<Button>("ColumnsButton")!.Focus(NavigationMethod.Tab);
            Press(window, Key.Enter);
            await UntilAsync(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            var choice = Named<ComboBox>(dialog, "ColumnChoice");
            Assert.True(choice.IsFocused);
            Press(dialog, Key.Down);
            Assert.Equal("Id", ((DataGridColumn)choice.SelectedItem!).Header);
            Named<Button>(dialog, "ColumnLeft").Focus(NavigationMethod.Tab);
            Press(dialog, Key.Enter);
            Assert.Equal(0, grid.Columns[1].DisplayIndex);
            var width = Named<NumericUpDown>(dialog, "ColumnWidth");
            width.Focus();
            Press(dialog, Key.Up);
            Assert.True(grid.Columns[1].Width.Value > 110);
            Named<Button>(dialog, "ColumnAscending").Focus();
            Press(dialog, Key.Enter);
            Assert.Equal(2, grid.ItemsSource!.Cast<ConsoleRow>().First().Cells["Id"].Value);
            Named<CheckBox>(dialog, "ColumnVisible").Focus();
            Press(dialog, Key.Space);
            Assert.False(grid.Columns[1].IsVisible);
            Named<Button>(dialog, "ColumnDefaults").Focus();
            Press(dialog, Key.Enter);
            Assert.True(grid.Columns.All(column => column.IsVisible));
            Assert.Equal(1, grid.Columns[1].DisplayIndex);
            Assert.Equal(110, grid.Columns[1].Width.Value);
            Named<CheckBox>(dialog, "ColumnVisible").Focus();
            Press(dialog, Key.Space);
            choice.SelectedItem = grid.Columns[0];
            Named<CheckBox>(dialog, "ColumnVisible").Focus();
            Press(dialog, Key.Space);
            Assert.True(grid.Columns[0].IsVisible);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Keep at least one column visible.");
            Press(dialog, Key.Escape);
            await UntilAsync(() => !window.OwnedWindows.Any());
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1.5)]
    [InlineData(2)]
    public async Task HighContrastNamesFocusAndLongPromptsRemainReadable(double scale)
    {
        var window = new MainWindow(new FixtureSession()) { Width = 1000, Height = 680, RequestedThemeVariant = App.HighContrastTheme };
        TestNavigation.ShowResource(window);
        try
        {
            window.SetRenderScaling(scale);
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var filter = window.FindControl<TextBox>("FilterBox")!;
            Assert.Equal("Filter displayed objects", ControlAutomationPeer.CreatePeerForElement(filter)!.GetName());
            Assert.Equal(AutomationLiveSetting.Polite, ControlAutomationPeer.CreatePeerForElement(window.FindControl<TextBlock>("StatusText")!)!.GetLiveSetting());
            window.FindControl<Button>("ColumnsButton")!.Focus(NavigationMethod.Tab);
            Assert.Contains(":focus-visible", window.FindControl<Button>("ColumnsButton")!.Classes);
            Assert.Equal(Colors.Black, ((ISolidColorBrush)window.FindControl<DataGrid>("ResultsGrid")!.Background!).Color);
            var grid = window.FindControl<DataGrid>("ResultsGrid")!;
            grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
            var row = grid.GetVisualDescendants().OfType<DataGridRow>()
                .Single(item => item.DataContext is ConsoleRow { Label: "alpha" });
            var peer = ControlAutomationPeer.CreatePeerForElement(row)!;
            Assert.Equal("alpha", peer.GetName());
            Assert.Equal("Selected", peer.GetItemStatus());
            grid.SelectedItems.Clear();
            Assert.Equal("Not selected", peer.GetItemStatus());
            grid.SelectedItem = grid.ItemsSource!.Cast<ConsoleRow>().First();
            Capture(window, $"high-contrast-1000x680-{scale * 100:0}");
            var prompt = new HostPrompt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), HostPromptKind.Fields,
                "Benign fixture prompt", "Complete all required fields. " + string.Join(" ", Enumerable.Repeat("Original sample guidance.", 20)),
                Enumerable.Range(1, 12).Select(index => new HostField($"Field{index}", $"Long fixture field label {index}",
                    "System.String", true, Help: "Use a benign sample value.")).ToArray(), [], -1,
                _ => Task.FromResult<string?>(null));
            var pending = Dialogs.HostPromptAsync(window, prompt, CancellationToken.None);
            var dialog = window.OwnedWindows.Single();
            dialog.SetRenderScaling(scale);
            Assert.Equal(App.HighContrastTheme, dialog.ActualThemeVariant);
            Assert.True(Named<TextBox>(dialog, "Field1").IsFocused);
            Assert.Equal("Long fixture field label 1 (required)", ControlAutomationPeer.CreatePeerForElement(Named<TextBox>(dialog, "Field1"))!.GetName());
            Named<Button>(dialog, "PromptCancel").Focus(NavigationMethod.Tab);
            window.UpdateLayout();
            dialog.UpdateLayout();
            Assert.True(dialog.Bounds.Height <= window.ClientSize.Height);
            AssertFits(Named<Button>(dialog, "PromptCancel"), dialog);
            Capture(dialog, $"prompt-{scale * 100:0}");
            Press(dialog, Key.Escape);
            Assert.Null(await pending);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ResetLayoutIsInvokableFromKeyboardMenu()
    {
        var window = new MainWindow(new FixtureSession());
        TestNavigation.ShowResource(window);
        try
        {
            await UntilAsync(() => !((ConsoleViewModel)window.DataContext!).IsBusy);
            var workspace = window.FindControl<Grid>("WorkspaceGrid")!;
            workspace.ColumnDefinitions[0].Width = new GridLength(300);
            workspace.ColumnDefinitions[4].Width = new GridLength(300);
            window.FindControl<Border>("DiagnosticsPane")!.IsVisible = true;
            window.FindControl<TextBox>("FilterBox")!.Focus();
            Press(window, Key.V, RawInputModifiers.Alt);
            Assert.True(window.FindControl<Menu>("MainMenu")!.IsOpen);
            Press(window, Key.L);
            Assert.Equal(220, workspace.ColumnDefinitions[0].Width.Value);
            Assert.Equal(220, workspace.ColumnDefinitions[4].Width.Value);
            Assert.False(window.FindControl<Border>("DiagnosticsPane")!.IsVisible);
            Press(window, Key.V, RawInputModifiers.Alt);
            Press(window, Key.C);
            Assert.True(window.FindControl<MenuItem>("HighContrastMenu")!.IsChecked);
            Assert.Equal(App.HighContrastTheme, window.ActualThemeVariant);
            Press(window, Key.V, RawInputModifiers.Alt);
            Press(window, Key.C);
            Assert.False(window.FindControl<MenuItem>("HighContrastMenu")!.IsChecked);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, window.ActualThemeVariant);
        }
        finally { window.Close(); }
    }
}
