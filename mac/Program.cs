using Avalonia;

namespace KawaPaint.Mac;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (KawaPaint.Cli.BatchCliRunner.IsBatchInvocation(args))
            return KawaPaint.Cli.BatchCliRunner.Run(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<KawaPaint.App.App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
