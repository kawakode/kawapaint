// KawaPaint — persisted panel placement for the (modular) UI.

using System.IO;
using System.Text.Json;

namespace KawaPaint.App;

public sealed class UiLayout
{
    // Values: Left, Right, Top, Bottom, Hidden, Floating.
    public string Tools { get; set; } = "Left";
    public string Colors { get; set; } = "Bottom";
    public string Layers { get; set; } = "Right";
    public string ColorWheel { get; set; } = "Right";

    // Top-left position used only while the corresponding panel above is "Floating".
    public double ToolsX { get; set; } = 90;
    public double ToolsY { get; set; } = 60;
    public double ColorsX { get; set; } = 90;
    public double ColorsY { get; set; } = 420;
    public double LayersX { get; set; } = 760;
    public double LayersY { get; set; } = 60;
    public double ColorWheelX { get; set; } = 520;
    public double ColorWheelY { get; set; } = 60;

    public string GetPlace(string key) => key switch
    {
        "Tools" => Tools,
        "Colors" => Colors,
        "Layers" => Layers,
        "ColorWheel" => ColorWheel,
        _ => "Hidden"
    };

    public void SetPlace(string key, string place)
    {
        switch (key)
        {
            case "Tools": Tools = place; break;
            case "Colors": Colors = place; break;
            case "Layers": Layers = place; break;
            case "ColorWheel": ColorWheel = place; break;
        }
    }

    public (double X, double Y) GetFloatPos(string key) => key switch
    {
        "Tools" => (ToolsX, ToolsY),
        "Colors" => (ColorsX, ColorsY),
        "Layers" => (LayersX, LayersY),
        "ColorWheel" => (ColorWheelX, ColorWheelY),
        _ => (60, 60)
    };

    public void SetFloatPos(string key, double x, double y)
    {
        switch (key)
        {
            case "Tools": ToolsX = x; ToolsY = y; break;
            case "Colors": ColorsX = x; ColorsY = y; break;
            case "Layers": LayersX = x; LayersY = y; break;
            case "ColorWheel": ColorWheelX = x; ColorWheelY = y; break;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static UiLayout LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UiLayout>(File.ReadAllText(path)) ?? new UiLayout();
        }
        catch { /* fall through */ }
        return new UiLayout();
    }
}
