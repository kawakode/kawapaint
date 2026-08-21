// KawaPaint - one item pinned to the customizable dock (see MainView's "Dock" panel).
//
// Serialized as a plain string into WorkspaceSettings.DockCommands so the settings schema didn't
// need a new collection type: a command id is stored bare, a color gets a "color:" prefix.

using System;

namespace KawaPaint.App.Core;

public enum DockEntryKind
{
    Command,
    Color
}

public readonly record struct DockEntry(DockEntryKind Kind, string Value)
{
    private const string ColorPrefix = "color:";

    public static DockEntry ForCommand(string commandId) => new(DockEntryKind.Command, commandId);
    public static DockEntry ForColor(string hex) => new(DockEntryKind.Color, hex);

    public string Serialize() => Kind == DockEntryKind.Color ? ColorPrefix + Value : Value;

    public static DockEntry Parse(string raw) =>
        raw.StartsWith(ColorPrefix, StringComparison.Ordinal)
            ? new DockEntry(DockEntryKind.Color, raw[ColorPrefix.Length..])
            : new DockEntry(DockEntryKind.Command, raw);
}
