// KawaPaint — one addressable action.
//
// Menus, the tool strip, keyboard shortcuts, the customizable dock and (later) plugin
// contributions are all views over the registry rather than separate wiring, so an action added
// once shows up everywhere it is relevant.

using System;
using Avalonia.Input;

namespace KawaPaint.App.Core;

public sealed class AppCommand
{
    public AppCommand(string id, string label, Action execute)
    {
        Id = id;
        Label = label;
        _execute = execute;
    }

    private readonly Action _execute;

    /// <summary>Stable dotted identifier, e.g. "file.open" or "view.zoom.fit".</summary>
    public string Id { get; }

    public string Label { get; }

    /// <summary>Groups the command in the dock's picker and the keyboard settings list.</summary>
    public string Category { get; init; } = "General";

    /// <summary>Icon name resolved through <see cref="Icons"/>. Commands without one show their label.</summary>
    public string? IconName { get; init; }

    /// <summary>Shipped default. The user's override lives in <see cref="WorkspaceSettings.KeyBindings"/>.</summary>
    public KeyGesture? DefaultGesture { get; init; }

    /// <summary>Longer text for tooltips; falls back to the label.</summary>
    public string? Description { get; init; }

    /// <summary>Null means always enabled.</summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>
    /// True for commands that must not fire while a text field has focus — Ctrl+A and Ctrl+Z
    /// belong to the text box in that situation.
    /// </summary>
    public bool SuppressInTextInput { get; init; }

    public bool IsEnabled => CanExecute?.Invoke() ?? true;

    public void Execute()
    {
        if (!IsEnabled) return;
        _execute();
    }
}
