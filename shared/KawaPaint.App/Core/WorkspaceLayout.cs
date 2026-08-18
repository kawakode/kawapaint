// KawaPaint — panel placement, keyed by panel id rather than by hardcoded property.
//
// Replaces the four fixed properties of the original UiLayout: adding a panel is now a
// registration, and an unknown id in a saved layout is simply ignored rather than being a
// deserialization hazard.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KawaPaint.App.Core;

[JsonConverter(typeof(JsonStringEnumConverter<PanelPlace>))]
public enum PanelPlace
{
    Left,
    Right,
    Top,
    Bottom,
    Floating,
    Hidden
}

/// <summary>Where one panel sits, plus enough memory to put it back when it is hidden or docked.</summary>
public sealed class PanelPlacement
{
    public PanelPlace Place { get; set; } = PanelPlace.Left;

    /// <summary>Last placement that was not Hidden, so the visibility toggle can restore it.</summary>
    public PanelPlace LastShown { get; set; } = PanelPlace.Left;

    /// <summary>Last docked side, so the float toggle has somewhere to send the panel back to.</summary>
    public PanelPlace LastDock { get; set; } = PanelPlace.Left;

    public double FloatX { get; set; } = 60;
    public double FloatY { get; set; } = 60;

    /// <summary>NaN means "size to content" — set once the user resizes the floating panel.</summary>
    public double FloatWidth { get; set; } = double.NaN;
    public double FloatHeight { get; set; } = double.NaN;

    /// <summary>
    /// Width when docked left or right, height when docked top or bottom. NaN falls back to the
    /// descriptor's default.
    /// </summary>
    public double DockSize { get; set; } = double.NaN;

    public PanelPlacement Clone() => new()
    {
        Place = Place,
        LastShown = LastShown,
        LastDock = LastDock,
        FloatX = FloatX,
        FloatY = FloatY,
        FloatWidth = FloatWidth,
        FloatHeight = FloatHeight,
        DockSize = DockSize
    };
}

/// <summary>A named arrangement of every registered panel.</summary>
public sealed class WorkspaceLayout
{
    public Dictionary<string, PanelPlacement> Panels { get; set; } = new();

    /// <summary>Placement for <paramref name="panelId"/>, created from the descriptor on first use.</summary>
    public PanelPlacement For(PanelDescriptor descriptor)
    {
        if (Panels.TryGetValue(descriptor.Id, out var existing)) return existing;

        var created = new PanelPlacement
        {
            Place = descriptor.DefaultPlace,
            // A panel that starts Hidden has no "shown" placement to remember yet — falling back
            // to Hidden here would make its very first toggle-visible a permanent no-op, since
            // ToggleVisible restores to LastShown. Floating is always a safe first placement.
            LastShown = descriptor.DefaultPlace == PanelPlace.Hidden ? PanelPlace.Floating : descriptor.DefaultPlace,
            LastDock = descriptor.DefaultPlace is PanelPlace.Floating or PanelPlace.Hidden
                ? PanelPlace.Left
                : descriptor.DefaultPlace,
            FloatX = descriptor.DefaultFloatX,
            FloatY = descriptor.DefaultFloatY,
            DockSize = descriptor.DefaultDockSize
        };
        Panels[descriptor.Id] = created;
        return created;
    }

    public WorkspaceLayout Clone()
    {
        var copy = new WorkspaceLayout();
        foreach (var (id, placement) in Panels) copy.Panels[id] = placement.Clone();
        return copy;
    }
}
