// KawaPaint — persisted panel placement for the (modular) UI.

using System.IO;
using System.Text.Json;

namespace KawaPaint.App;

public sealed class UiLayout
{
    // Values: Left, Right, Top, Bottom, Hidden.
    public string Tools { get; set; } = "Left";
    public string Colors { get; set; } = "Bottom";
    public string Layers { get; set; } = "Right";
    public string ColorWheel { get; set; } = "Right";

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
