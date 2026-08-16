// KawaPaint — a named color palette, persisted as JSON (.kwpal).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KawaPaint.Engine;

namespace KawaPaint.App;

public sealed class PaletteEntry
{
    public uint Bgra { get; set; }
    public string? Name { get; set; }

    public ColorBgra Color => ColorBgra.FromUInt32(Bgra);
}

public sealed class Palette
{
    public List<PaletteEntry> Colors { get; set; } = new();

    public void Add(ColorBgra color, string? name = null) =>
        Colors.Add(new PaletteEntry { Bgra = color.ToUInt32(), Name = name });

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Palette LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Palette>(File.ReadAllText(path)) ?? Default();
        }
        catch { /* fall through to default */ }
        return Default();
    }

    public static Palette Default()
    {
        var p = new Palette();
        (string name, byte r, byte g, byte b)[] seed =
        {
            ("Black", 0, 0, 0), ("White", 255, 255, 255), ("Gray", 128, 128, 128),
            ("Red", 224, 16, 16), ("Orange", 240, 140, 20), ("Yellow", 245, 220, 30),
            ("Green", 32, 176, 32), ("Teal", 20, 170, 160), ("Blue", 32, 80, 224),
            ("Navy", 20, 30, 120), ("Purple", 130, 40, 190), ("Pink", 235, 110, 180)
        };
        foreach (var (name, r, g, b) in seed)
            p.Add(ColorBgra.FromBgr(b, g, r), name);
        return p;
    }
}
