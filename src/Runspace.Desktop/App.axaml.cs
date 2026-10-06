using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Runspace.Desktop;

public partial class App : Application
{
    public static ThemeVariant HighContrastTheme { get; } = new("HighContrast", ThemeVariant.Dark);
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = Program.Benchmark is { } benchmark
                ? new MainWindow(benchmark.Session) { Width = 1200, Height = 800 }
                : Program.Validation is null ? new MainWindow() : new MainWindow(null, persistLayout: false);
            desktop.MainWindow = window;
            Program.Validation?.Attach(window);
            Program.Benchmark?.Attach(window);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
