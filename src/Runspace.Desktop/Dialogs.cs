using System.Security;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Runspace.Core;

namespace Runspace.Desktop;

internal static class Dialogs
{
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
        dialog.Content = panel;
        return await ShowPromptAsync<bool>(dialog, owner, cancellationToken);
    }

    public static async Task<IReadOnlyDictionary<string, string>?> ParametersAsync(
        Window owner, ConsoleAction action, string context, IReadOnlyList<ProviderInfo>? providers = null, string? currentValue = null,
        CancellationToken cancellationToken = default)
    {
        var dialog = Create(owner, action.Name, 480);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = context, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#59636E") });
        var inputs = new Dictionary<string, Control>();
        foreach (var parameter in action.Parameters ?? [])
        {
            panel.Children.Add(new TextBlock { Text = parameter.Label });
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
            panel.Children.Add(input);
        }
        var error = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };
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
        dialog.Content = panel;
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
            panel.Children.Add(choice);
            var help = new TextBlock { TextWrapping = TextWrapping.Wrap };
            choice.SelectionChanged += (_, _) => help.Text = choice.SelectedIndex >= 0 ? prompt.Choices[choice.SelectedIndex].Help : null;
            panel.Children.Add(help);
        }
        foreach (var field in prompt.Fields)
        {
            panel.Children.Add(new TextBlock { Text = $"{field.Label} ({field.TypeName})" + (field.Required ? " *" : string.Empty) });
            var credential = field.TypeName == "System.Management.Automation.PSCredential";
            var secret = field.TypeName == "System.Security.SecureString";
            var input = new TextBox { Name = field.Name, Text = secret ? null : field.DefaultValue, PasswordChar = secret ? '*' : '\0',
                PlaceholderText = credential ? "User name" : null, IsReadOnly = field.IsReadOnly };
            panel.Children.Add(input);
            TextBox? password = null;
            if (credential)
            {
                password = new TextBox { Name = field.Name + "Password", PasswordChar = '*', PlaceholderText = "Password" };
                panel.Children.Add(password);
            }
            editors.Add(field.Name, (input, password));
            if (!string.IsNullOrWhiteSpace(field.Help))
                panel.Children.Add(new TextBlock { Text = field.Help, TextWrapping = TextWrapping.Wrap });
        }
        var error = new TextBlock { Name = "PromptError", Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };
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
            MinWidth = 450, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel { Margin = new Thickness(10) };
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        var details = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 85, Margin = new Thickness(0, 8, 0, 0),
            PlaceholderText = "Select a property to read and copy its complete value."
        };
        DockPanel.SetDock(details, Dock.Bottom);
        panel.Children.Add(details);
        var grid = new DataGrid { ItemsSource = properties, AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column };
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
        window.Content = panel;
        await window.ShowDialog(owner);
    }

    private static PromptWindow Create(Window owner, string title, double width) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height,
        MaxHeight = Math.Max(400, double.IsFinite(owner.Height) ? owner.Height - 40 : 600), CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    };

    private static StackPanel Buttons() => new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
}
