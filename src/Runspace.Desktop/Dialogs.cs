using System.Security;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Runspace.Core;

namespace Runspace.Desktop;

internal static class Dialogs
{
    public static async Task<UnsavedScriptChoice> UnsavedScriptAsync(Window owner, string name)
    {
        var dialog = Create(owner, "Unsaved script", 500);
        dialog.Name = "UnsavedScriptDialog";
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = $"Save changes to {name} before continuing?", TextWrapping = TextWrapping.Wrap });
        var buttons = Buttons();
        foreach (var (caption, choice) in new[]
        {
            ("Cancel", UnsavedScriptChoice.Cancel), ("Discard", UnsavedScriptChoice.Discard), ("Save", UnsavedScriptChoice.Save)
        })
        {
            var button = new Button { Name = "Script" + caption, Content = caption, IsCancel = choice == UnsavedScriptChoice.Cancel };
            button.Click += (_, _) => dialog.Complete(choice);
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return await ShowPromptAsync<UnsavedScriptChoice>(dialog, owner, CancellationToken.None);
    }

    public static async Task<bool> OverwriteScriptAsync(Window owner, string path)
    {
        var dialog = Create(owner, "Replace script file", 500);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = $"Replace the existing file at {path}?", TextWrapping = TextWrapping.Wrap });
        var buttons = Buttons();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var save = new Button { Content = "Replace" };
        cancel.Click += (_, _) => dialog.Complete(false);
        save.Click += (_, _) => dialog.Complete(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return await ShowPromptAsync<bool>(dialog, owner, CancellationToken.None);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, CancellationToken cancellationToken = default)
    {
        var dialog = Create(owner, title, 500);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = Buttons();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var accept = new Button { Content = "Execute" };
        cancel.Click += (_, _) => dialog.Complete(false);
        accept.Click += (_, _) => dialog.Complete(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        panel.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = panel };
        return await ShowPromptAsync<bool>(dialog, owner, cancellationToken);
    }

    public static async Task<IReadOnlyDictionary<string, string>?> ParametersAsync(
        Window owner, ConsoleAction action, string context, IReadOnlyList<ProviderInfo>? providers = null, string? currentValue = null,
        CancellationToken cancellationToken = default)
    {
        var dialog = Create(owner, action.Name, 480);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        var contextText = new TextBlock { Text = context, TextWrapping = TextWrapping.Wrap };
        contextText.Bind(TextBlock.ForegroundProperty, owner.GetResourceObservable("ConsoleMuted"));
        panel.Children.Add(contextText);
        var inputs = new Dictionary<string, Control>();
        foreach (var parameter in action.Parameters ?? [])
        {
            panel.Children.Add(new TextBlock { Text = parameter.Label, TextWrapping = TextWrapping.Wrap });
            Control input;
            if (parameter.Choices is { Count: > 0 } choices)
                input = new ComboBox { ItemsSource = choices, SelectedItem = parameter.DefaultValue, HorizontalAlignment = HorizontalAlignment.Stretch };
            else if (parameter.Name == "Provider" && providers is { Count: > 0 })
            {
                var names = providers.Select(provider => provider.Name).ToArray();
                input = new ComboBox { ItemsSource = names, SelectedItem = names.Contains("FileSystem") ? "FileSystem" : names[0], HorizontalAlignment = HorizontalAlignment.Stretch };
            }
            else
                input = new TextBox { Text = parameter.Name == "Value" ? currentValue : parameter.DefaultValue };
            inputs.Add(parameter.Name, input);
            input.Name = parameter.Name;
            AutomationProperties.SetName(input, parameter.Label + (parameter.Required ? " (required)" : string.Empty));
            panel.Children.Add(input);
        }
        var error = ErrorText(owner);
        panel.Children.Add(error);
        var buttons = Buttons();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var accept = new Button { Content = "OK", IsDefault = true };
        cancel.Click += (_, _) => dialog.Close();
        accept.Click += (_, _) =>
        {
            var values = inputs.ToDictionary(pair => pair.Key, pair => pair.Value switch
            {
                TextBox text => text.Text ?? string.Empty,
                ComboBox choice => choice.SelectedItem as string ?? string.Empty,
                _ => throw new InvalidOperationException("Unsupported parameter editor.")
            });
            var missing = action.Parameters?.FirstOrDefault(parameter => parameter.Required && string.IsNullOrWhiteSpace(values[parameter.Name]));
            if (missing is not null)
            {
                error.Text = $"{missing.Label} is required.";
                return;
            }
            dialog.Complete(values);
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        panel.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = panel };
        return await ShowPromptAsync<IReadOnlyDictionary<string, string>?>(dialog, owner, cancellationToken);
    }

    public static async Task<HostResponse?> HostPromptAsync(Window owner, HostPrompt prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = Create(owner, string.IsNullOrWhiteSpace(prompt.Caption) ? "PowerShell input" : prompt.Caption, 520);
        dialog.Name = "InvocationPrompt";
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = prompt.Message, TextWrapping = TextWrapping.Wrap });
        var editors = new Dictionary<string, (TextBox Value, TextBox? Password)>();
        ComboBox? choice = null;
        if (prompt.Kind == HostPromptKind.Choice)
        {
            choice = new ComboBox
            {
                Name = "PromptChoice", ItemsSource = prompt.Choices.Select(item => item.Label.Replace("&", string.Empty)).ToArray(),
                SelectedIndex = prompt.DefaultChoice, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(choice, string.IsNullOrWhiteSpace(prompt.Message) ? "PowerShell choice" : prompt.Message);
            panel.Children.Add(choice);
            var help = new TextBlock { TextWrapping = TextWrapping.Wrap };
            choice.SelectionChanged += (_, _) => help.Text = choice.SelectedIndex >= 0 ? prompt.Choices[choice.SelectedIndex].Help : null;
            panel.Children.Add(help);
        }
        foreach (var field in prompt.Fields)
        {
            panel.Children.Add(new TextBlock { Text = $"{field.Label} ({field.TypeName})" + (field.Required ? " *" : string.Empty),
                TextWrapping = TextWrapping.Wrap });
            var credential = field.TypeName == "System.Management.Automation.PSCredential";
            var secret = field.TypeName == "System.Security.SecureString";
            var input = new TextBox { Name = field.Name, Text = secret ? null : field.DefaultValue, PasswordChar = secret ? '*' : '\0',
                PlaceholderText = credential ? "User name" : null, IsReadOnly = field.IsReadOnly };
            panel.Children.Add(input);
            AutomationProperties.SetName(input, field.Label + (credential ? " user name" : string.Empty) + (field.Required ? " (required)" : string.Empty));
            AutomationProperties.SetHelpText(input, field.Help ?? string.Empty);
            TextBox? password = null;
            if (credential)
            {
                password = new TextBox { Name = field.Name + "Password", PasswordChar = '*', PlaceholderText = "Password" };
                panel.Children.Add(password);
                AutomationProperties.SetName(password, field.Label + " password");
            }
            editors.Add(field.Name, (input, password));
            if (!string.IsNullOrWhiteSpace(field.Help))
                panel.Children.Add(new TextBlock { Text = field.Help, TextWrapping = TextWrapping.Wrap });
        }
        var error = ErrorText(owner);
        error.Name = "PromptError";
        panel.Children.Add(error);
        var buttons = Buttons();
        var cancel = new Button { Name = "PromptCancel", Content = "Cancel", IsCancel = true };
        var accept = new Button { Name = "PromptAccept", Content = "OK", IsDefault = true };
        cancel.Click += (_, _) => dialog.Close();
        accept.Click += async (_, _) =>
        {
            var values = new Dictionary<string, object?>();
            foreach (var field in prompt.Fields)
            {
                var editor = editors[field.Name];
                if (field.Required && (string.IsNullOrWhiteSpace(editor.Value.Text) ||
                    editor.Password is not null && string.IsNullOrEmpty(editor.Password.Text)))
                {
                    error.Text = $"{field.Label} is required.";
                    new HostResponse(values).Dispose();
                    return;
                }
                values[field.Name] = editor.Password is not null
                    ? new HostCredential(editor.Value.Text ?? string.Empty, Secure(editor.Password.Text))
                    : field.TypeName == "System.Security.SecureString" ? Secure(editor.Value.Text) : editor.Value.Text ?? string.Empty;
            }
            var response = new HostResponse(values, choice?.SelectedIndex);
            accept.IsEnabled = false;
            try
            {
                var validation = await prompt.ValidateAsync(response);
                if (cancellationToken.IsCancellationRequested || !dialog.IsVisible) { response.Dispose(); return; }
                if (validation is not null) { error.Text = validation; response.Dispose(); return; }
                dialog.Complete(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { response.Dispose(); }
            catch (Exception)
            {
                response.Dispose();
                dialog.Failure = new InvalidOperationException("Host input validation failed.");
                dialog.Close();
            }
            finally { accept.IsEnabled = true; }
        };
        var stop = new Button { Name = "PromptStop", Content = "Stop invocation" };
        stop.Click += (_, _) => dialog.Close();
        buttons.Children.Add(stop);
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        panel.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = panel };
        dialog.Closed += (_, _) =>
        {
            foreach (var editor in editors.Values)
            {
                editor.Value.Text = string.Empty;
                if (editor.Password is not null) editor.Password.Text = string.Empty;
            }
        };
        return await ShowPromptAsync<HostResponse?>(dialog, owner, cancellationToken);
    }

    private static SecureString Secure(string? text)
    {
        var secure = new SecureString();
        foreach (var character in text ?? string.Empty) secure.AppendChar(character);
        secure.MakeReadOnly();
        return secure;
    }

    private sealed class PromptWindow : Window
    {
        public object? Response { get; private set; }
        public Exception? Failure { get; set; }
        public void Complete(object response) { Response = response; Close(); }
    }

    private static async Task<T?> ShowPromptAsync<T>(PromptWindow dialog, Window owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => completion.TrySetResult(dialog.Response is T response ? response : default);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
        FocusFirstEditor(dialog);
        dialog.Show(owner);
        if (cancellationToken.IsCancellationRequested) dialog.Close();
        var result = await completion.Task;
        if (dialog.Failure is not null) throw dialog.Failure;
        return result;
    }

    public static async Task PropertiesAsync(Window owner, string name, IReadOnlyList<ObjectProperty> properties)
    {
        var window = new Window
        {
            Title = $"{name} - Properties", Width = 750, Height = 520,
            MinWidth = 450, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant
        };
        var panel = new DockPanel { Margin = new Thickness(10) };
        var close = new Button { Name = "PropertiesClose", Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        var details = new TextBox
        {
            Name = "PropertyValue", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 85, Margin = new Thickness(0, 8, 0, 0),
            PlaceholderText = "Select a property to read and copy its complete value."
        };
        AutomationProperties.SetName(details, "Complete property value");
        DockPanel.SetDock(details, Dock.Bottom);
        panel.Children.Add(details);
        var grid = new DataGrid { Name = "PropertiesGrid", ItemsSource = properties, AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column };
        AutomationProperties.SetName(grid, "Original object properties");
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is ObjectProperty property)
                details.Text = $"{property.Name} ({property.Type})\n{property.Value}" + (property.Error is null ? string.Empty : $"\nError: {property.Error}");
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Property", Binding = new Binding(nameof(ObjectProperty.Name)), Width = new DataGridLength(170) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(ObjectProperty.Type)), Width = new DataGridLength(150) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding(nameof(ObjectProperty.Value)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Error", Binding = new Binding(nameof(ObjectProperty.Error)), Width = new DataGridLength(180), IsVisible = properties.Any(property => property.Error is not null) });
        panel.Children.Add(grid);
        window.Content = panel;
        window.Opened += (_, _) => { grid.SelectedItem = properties.FirstOrDefault(); grid.Focus(); };
        await window.ShowDialog(owner);
    }

    public static async Task MessageAsync(Window owner, string title, string message)
    {
        var window = Create(owner, title, 500);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();
        panel.Children.Add(close);
        window.Content = new ScrollViewer { Content = panel };
        window.Opened += (_, _) => close.Focus();
        await window.ShowDialog(owner);
    }

    private static PromptWindow Create(Window owner, string title, double width) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height,
        MaxHeight = Math.Max(400, double.IsFinite(owner.Height) ? owner.Height - 40 : 600), CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = owner.ActualThemeVariant
    };

    private static TextBlock ErrorText(Window owner)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.ForegroundProperty, owner.GetResourceObservable("ConsoleError"));
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Assertive);
        return text;
    }

    private static void FocusFirstEditor(Window window) => window.Opened += (_, _) =>
        (window.GetLogicalDescendants().OfType<Control>().FirstOrDefault(control =>
            control.IsEnabled && control is TextBox or ComboBox) ??
         window.GetLogicalDescendants().OfType<Button>().FirstOrDefault())?.Focus();

    public static async Task ColumnsAsync(Window owner, DataGrid grid, IReadOnlyList<ConsoleColumn> defaults)
    {
        var window = Create(owner, "Columns", 480);
        window.Name = "ColumnsDialog";
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Choose a column to show, resize, move, or sort.", TextWrapping = TextWrapping.Wrap });
        var columns = new ComboBox { Name = "ColumnChoice", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DataGridColumn>((column, _) =>
                new TextBlock { Text = column?.Header?.ToString() }) };
        AutomationProperties.SetName(columns, "Column");
        var visible = new CheckBox { Name = "ColumnVisible", Content = "Visible" };
        var width = new NumericUpDown { Name = "ColumnWidth", Minimum = 40, Maximum = 1200, Increment = 10 };
        AutomationProperties.SetName(width, "Column width in device-independent pixels");
        var error = ErrorText(owner);
        var updating = false;
        void UpdateEditors()
        {
            updating = true;
            if (columns.SelectedItem is DataGridColumn column)
            {
                visible.IsChecked = column.IsVisible;
                width.Value = (decimal)Math.Clamp(column.ActualWidth, 40, 1200);
            }
            updating = false;
        }
        void RefreshColumns(DataGridColumn? selected)
        {
            columns.ItemsSource = grid.Columns.OrderBy(column => column.DisplayIndex).ToArray();
            columns.SelectedItem = selected ?? grid.Columns.FirstOrDefault();
            UpdateEditors();
        }
        columns.SelectionChanged += (_, _) => UpdateEditors();
        visible.IsCheckedChanged += (_, _) =>
        {
            if (updating || columns.SelectedItem is not DataGridColumn column) return;
            if (visible.IsChecked != true && grid.Columns.Count(item => item.IsVisible) == 1)
            {
                error.Text = "Keep at least one column visible.";
                updating = true;
                visible.IsChecked = true;
                updating = false;
                return;
            }
            error.Text = string.Empty;
            column.IsVisible = visible.IsChecked == true;
        };
        width.ValueChanged += (_, _) =>
        {
            if (!updating && width.Value is { } value && columns.SelectedItem is DataGridColumn column)
                column.Width = new DataGridLength((double)value);
        };
        Button Command(string name, string caption, Action<DataGridColumn> command)
        {
            var button = new Button { Name = name, Content = caption };
            button.Click += (_, _) => { if (columns.SelectedItem is DataGridColumn column) command(column); };
            return button;
        }
        var commands = new WrapPanel { Orientation = Orientation.Horizontal };
        commands.Children.Add(Command("ColumnLeft", "Move left", column =>
        {
            if (column.DisplayIndex > 0) column.DisplayIndex--;
            RefreshColumns(column);
        }));
        commands.Children.Add(Command("ColumnRight", "Move right", column =>
        {
            if (column.DisplayIndex < grid.Columns.Count - 1) column.DisplayIndex++;
            RefreshColumns(column);
        }));
        commands.Children.Add(Command("ColumnAscending", "Sort ascending", column => column.Sort(ListSortDirection.Ascending)));
        commands.Children.Add(Command("ColumnDescending", "Sort descending", column => column.Sort(ListSortDirection.Descending)));
        var restore = new Button { Name = "ColumnDefaults", Content = "Restore default columns" };
        restore.Click += (_, _) =>
        {
            foreach (var column in grid.Columns)
            {
                var definition = defaults.Single(item => item.Key == column.Tag?.ToString());
                column.IsVisible = true;
                column.DisplayIndex = defaults.ToList().IndexOf(definition);
                column.Width = new DataGridLength(definition.Kind is ColumnKind.Number or ColumnKind.Bytes or ColumnKind.Boolean
                    ? Math.Min(definition.Width, 110) : definition.Width);
            }
            error.Text = string.Empty;
            RefreshColumns(columns.SelectedItem as DataGridColumn);
        };
        var close = new Button { Name = "ColumnsClose", Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => window.Close();
        panel.Children.Add(columns);
        panel.Children.Add(visible);
        panel.Children.Add(new TextBlock { Text = "Width (DIP)" });
        panel.Children.Add(width);
        panel.Children.Add(commands);
        panel.Children.Add(restore);
        panel.Children.Add(error);
        panel.Children.Add(close);
        window.Content = new ScrollViewer { Content = panel };
        RefreshColumns(null);
        window.Opened += (_, _) => columns.Focus();
        await window.ShowDialog(owner);
    }

    private static StackPanel Buttons() => new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
}
