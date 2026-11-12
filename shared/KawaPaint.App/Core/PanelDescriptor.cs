// KawaPaint - what the docking framework needs to know about a panel.

using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace KawaPaint.App.Core;

/// <summary>
/// Registration record for one dockable panel. The content is supplied as an already-built
/// control so panels declared in AXAML and panels built in code register the same way.
/// </summary>
public sealed class PanelDescriptor
{
    public PanelDescriptor(string id, string title, Control content)
    {
        Id = id;
        Title = title;
        Content = content;
    }

    public string Id { get; }

    /// <summary>Shown on the floating title bar and in the View menu.</summary>
    public string Title { get; }

    public Control Content { get; }

    /// <summary>Icon name resolved through <see cref="Icons"/>; null hides the toggle button.</summary>
    public string? IconName { get; init; }

    /// <summary>
    /// In-panel chrome that duplicates the floating title bar - the panel's own caption and its
    /// float/close buttons. Hidden while the panel floats, restored when it docks again.
    /// </summary>
    public IReadOnlyList<Control> DockedChrome { get; init; } = Array.Empty<Control>();

    public PanelPlace DefaultPlace { get; init; } = PanelPlace.Left;

    /// <summary>
    /// The side the panel docks to when it is not docked to start with: where the dock button of
    /// a panel that starts floating sends it, and where a panel that starts hidden first shows up.
    /// Null keeps the old behaviour - dock left, and a hidden panel's first appearance floats.
    /// </summary>
    public PanelPlace? HomeDock { get; init; }

    /// <summary>Width when docked left/right, height when docked top/bottom. NaN sizes to content.</summary>
    public double DefaultDockSize { get; init; } = double.NaN;

    /// <summary>
    /// With <see cref="DefaultFloatFromRight"/>, the gap between the panel's right edge and the
    /// window's instead of the distance from the left edge.
    /// </summary>
    public double DefaultFloatX { get; init; } = 60;
    public double DefaultFloatY { get; init; } = 60;
    public double DefaultFloatWidth { get; init; } = double.NaN;
    public double DefaultFloatHeight { get; init; } = double.NaN;

    /// <summary>
    /// Measure <see cref="DefaultFloatX"/> from the right edge, so a panel that starts floating
    /// sits in the top-right corner whatever the window size. The position is resolved once the
    /// window has a size (see PanelManager.ResolvePendingFloats) and then saved as usual; set
    /// <see cref="DefaultFloatWidth"/> too, since the panel's width decides where it starts.
    /// </summary>
    public bool DefaultFloatFromRight { get; init; }

    /// <summary>
    /// Floor for interactive resizing. The effective minimum is the larger of this and the
    /// content's own desired size, so a panel can never be dragged down to unusable.
    /// </summary>
    public double MinWidth { get; init; } = 120;
    public double MinHeight { get; init; } = 80;
}
