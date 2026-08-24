namespace KawaPaint.Engine.MailMerge;

public enum DynamicTextAlignment { Left, Center, Right }
public enum DynamicTextVerticalAlignment { Top, Center, Bottom }

/// <summary>An editable placeholder stored in a .kwp and rasterized only for a merge row.</summary>
public sealed class DynamicTextZone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Dynamic text";
    public string Template { get; set; } = "{Name}";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 300;
    public int Height { get; set; } = 80;
    public float FontSize { get; set; } = 48;
    public string? FontFamily { get; set; }
    public string Color { get; set; } = "FF000000";
    public DynamicTextAlignment Alignment { get; set; } = DynamicTextAlignment.Center;
    public DynamicTextVerticalAlignment VerticalAlignment { get; set; } = DynamicTextVerticalAlignment.Center;
    public bool Wrap { get; set; } = true;
    public bool ShrinkToFit { get; set; } = true;

    public DynamicTextZone Clone() => (DynamicTextZone)MemberwiseClone();
}

