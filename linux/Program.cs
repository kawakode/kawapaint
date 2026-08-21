using Avalonia;

namespace KawaPaint.Linux;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    public static int Main(string[] args)
    {
        if (KawaPaint.Cli.BatchCliRunner.IsBatchInvocation(args))
            return KawaPaint.Cli.BatchCliRunner.Run(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<KawaPaint.App.App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
