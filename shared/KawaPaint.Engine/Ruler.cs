// KawaPaint — pure math for ruler tick placement. No Avalonia dependency, so it's testable
// headlessly; the actual drawing lives in KawaPaint.App.RulerBar.

namespace KawaPaint.Engine;

public enum RulerUnit
{
    Pixels,
    Inches,
    Centimeters
}

public static class RulerMath
{
    /// <summary>How many document pixels make up one unit, at the document's resolution.</summary>
    public static double PixelsPerUnit(RulerUnit unit, double dpi) => unit switch
    {
        RulerUnit.Inches => dpi,
        RulerUnit.Centimeters => dpi / 2.54,
        _ => 1.0
    };

    /// <summary>
    /// Picks a "nice" (1/2/5 × 10^n) major-tick spacing, in the given unit, so that consecutive
    /// major ticks land at least <paramref name="minScreenSpacing"/> screen pixels apart at the
    /// current zoom. Used by both the horizontal and vertical ruler.
    /// </summary>
    public static double MajorStep(RulerUnit unit, double dpi, double zoom, double minScreenSpacing = 60)
    {
        double screenPxPerUnit = PixelsPerUnit(unit, dpi) * zoom;
        if (screenPxPerUnit <= 0 || double.IsNaN(screenPxPerUnit) || double.IsInfinity(screenPxPerUnit))
            return 1;

        double raw = minScreenSpacing / screenPxPerUnit;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));

        foreach (double m in NiceMultiples)
        {
            double step = m * magnitude;
            if (step >= raw) return step;
        }
        return 10 * magnitude;
    }

    private static readonly double[] NiceMultiples = { 1, 2, 5, 10 };

    /// <summary>How many minor ticks subdivide one major interval, for a given unit.</summary>
    public static int MinorSubdivisions(RulerUnit unit) => unit switch
    {
        RulerUnit.Inches => 4,   // quarter-inch minors, the conventional print-ruler subdivision
        _ => 10
    };

    /// <summary>Formats a tick's label. Pixels are whole numbers; physical units get one decimal.</summary>
    public static string FormatLabel(RulerUnit unit, double value) => unit switch
    {
        RulerUnit.Pixels => Math.Round(value).ToString("0"),
        _ => value.ToString("0.##")
    };

    public static string Abbreviation(RulerUnit unit) => unit switch
    {
        RulerUnit.Inches => "in",
        RulerUnit.Centimeters => "cm",
        _ => "px"
    };
}
