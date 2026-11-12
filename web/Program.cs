using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;

[assembly: SupportedOSPlatform("browser")]

namespace KawaPaint.Web;

internal sealed partial class Program
{
    private static Task Main(string[] args)
    {
        // Before the first view reads its settings.
        if (LocalStorageSettingsStore.TryCreate() is { } store)
            KawaPaint.App.Core.SettingsStore.Current = store;
        else
            KawaPaint.App.Core.SettingsStore.Current = new KawaPaint.App.Core.MemorySettingsStore();

        // Autosave snapshots go to IndexedDB, and the page asks before it is closed with unsaved
        // work. Without IndexedDB (some private modes) saving a snapshot fails and says so.
        KawaPaint.App.Core.RecoveryStore.Current = new BrowserRecoveryStore();
        KawaPaint.App.App.UnsavedChangesChanged = BrowserRecoveryStore.ReportUnsavedChanges;

        // A phone or tablet browser gets the touch interface the Android app uses, chosen the
        // same way (by the screen's shorter side); a mouse-driven browser keeps the desktop one.
        // CSS pixels are density-independent, like Android's dp.
        try
        {
            if (IsTouchFirst())
                KawaPaint.App.Core.DeviceInfo.SmallestScreenWidthDp = SmallestScreenWidth();
        }
        catch { /* desktop interface */ }
        return StartAsync();
    }

    [JSImport("device.isTouchFirst", "main.js")]
    private static partial bool IsTouchFirst();

    [JSImport("device.smallestScreenWidth", "main.js")]
    private static partial double SmallestScreenWidth();

    private static Task StartAsync() => BuildAvaloniaApp()
        .WithInterFont()
        .StartBrowserAppAsync("out", new BrowserPlatformOptions
        {
            // WebGL creation can be blocked by a GPU driver blocklist, an extension, or a VM/
            // software-render setup - and a failed WebGL attempt poisons the <canvas> element so
            // Avalonia's own fallback to a 2D context then also fails ("mode 3" in the console).
            // Software2D never touches WebGL, so it works everywhere at the cost of GPU accel.
            RenderingMode = new[] { BrowserRenderingMode.Software2D }
        });

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<KawaPaint.App.App>();
}
