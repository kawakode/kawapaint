using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;

[assembly: SupportedOSPlatform("browser")]

namespace KawaPaint.Web;

internal sealed partial class Program
{
    private static Task Main(string[] args) => BuildAvaloniaApp()
        .WithInterFont()
        .StartBrowserAppAsync("out", new BrowserPlatformOptions
        {
            // WebGL creation can be blocked by a GPU driver blocklist, an extension, or a VM/
            // software-render setup — and a failed WebGL attempt poisons the <canvas> element so
            // Avalonia's own fallback to a 2D context then also fails ("mode 3" in the console).
            // Software2D never touches WebGL, so it works everywhere at the cost of GPU accel.
            RenderingMode = new[] { BrowserRenderingMode.Software2D }
        });

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<KawaPaint.App.App>();
}
