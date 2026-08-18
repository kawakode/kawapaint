// KawaPaint — what the docking framework needs to know about a panel.

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
    /// In-panel chrome that duplicates the floating title bar — the panel's own caption and its
    /// float/close buttons. Hidden while the panel floats, restored when it docks again.
    /// </summary>
    public IReadOnlyList<Control> DockedChrome { get; init; } = Array.Empty<Control>();

    public PanelPlace DefaultPlace { get; init; } = PanelPlace.Left;

    /// <summary>Width when docked left/right, height when docked top/bottom. NaN sizes to content.</summary>
    public double DefaultDockSize { get; init; } = double.NaN;

    public double DefaultFloatX { get; init; } = 60;
    public double DefaultFloatY { get; init; } = 60;

    /// <summary>
    /// Floor for interactive resizing. The effective minimum is the larger of this and the
    /// content's own desired size, so a panel can never be dragged down to unusable.
    /// </summary>
    public double MinWidth { get; init; } = 120;
    public double MinHeight { get; init; } = 80;
}
