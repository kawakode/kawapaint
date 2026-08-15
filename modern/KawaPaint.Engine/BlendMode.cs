namespace KawaPaint.Engine;

/// <summary>Layer blend modes, mirroring the set from the original Paint.NET UserBlendOps.</summary>
public enum BlendMode
{
    Normal,
    Multiply,
    Additive,
    ColorBurn,
    ColorDodge,
    Reflect,
    Glow,
    Overlay,
    Difference,
    Negation,
    Lighten,
    Darken,
    Screen,
    Xor
}
