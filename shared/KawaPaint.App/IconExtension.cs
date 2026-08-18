// KawaPaint — lets AXAML reference an Icons.cs glyph directly (e.g. Icon="{app:Icon Save}")
// instead of wiring each control up by name in code-behind.

using System;
using Avalonia.Markup.Xaml;

namespace KawaPaint.App;

public sealed class IconExtension : MarkupExtension
{
    public string Key { get; set; }
    public double Size { get; set; } = 14;

    public IconExtension() : this(string.Empty) { }
    public IconExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Icons.Create(Key, Size);
}
