using Avalonia;
using System;
using System.Runtime.InteropServices;

namespace KawaPaint.Win;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (KawaPaint.Cli.BatchCliRunner.IsBatchInvocation(args))
        {
            // This host is built WinExe (no console window of its own) so a normal GUI launch
            // never pops a black box behind it - but that also means Console.WriteLine below would
            // vanish silently when run from an existing terminal. Attaching to that terminal's
            // console (if any) before the first Console call makes --script output show up there
            // the way a batch/CI invocation needs it to.
            AttachConsole(AttachParentProcess);
            return KawaPaint.Cli.BatchCliRunner.Run(args);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

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
