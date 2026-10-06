using Avalonia;

namespace Runspace.Desktop;

internal static class Program
{
    internal static PublishValidation? Validation { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--validate-publish")
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: Runspace.Desktop --validate-publish <report.json>");
                return 2;
            }
            Validation = new PublishValidation(System.IO.Path.GetFullPath(args[1]));
            Validation.CheckRuntimeAsync().GetAwaiter().GetResult();
            if (!Validation.RuntimePassed) return 1;
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
                return Validation.ExitCode;
            }
            catch (Exception exception)
            {
                Validation.FailDesktop(exception);
                return 1;
            }
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
#if DEBUG
        builder.WithDeveloperTools();
#endif
        return builder;
    }
}
