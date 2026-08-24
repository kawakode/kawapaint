// KawaPaint - one recorded input in a demo stream.
//
// The recorder captures *inputs*, not results: a stroke is the polyline the pointer traced, not
// the pixels it produced. Replaying the same inputs against the same starting document through
// the same engine reproduces the session, the way a Doom .lmp replays a level from its tic
// stream. That is what keeps a whole painting session down to a few kilobytes - and it is also
// what makes the starting document part of the format rather than an afterthought (see DemoFile).

using System.Collections.Generic;
using System.Linq;

namespace KawaPaint.App.Core.Demo;

public enum DemoOp : byte
{
    /// <summary>Left button down on the canvas, at an image-space coordinate.</summary>
    PointerDown = 0x01,

    /// <summary>Pointer dragged during a stroke. Only emitted while a stroke is in flight.</summary>
    PointerMove = 0x02,

    PointerUp = 0x03,

    /// <summary>Tool selection, by the same tag string the tool buttons use ("Pencil", "Brush").</summary>
    Tool = 0x10,

    /// <summary>Foreground or background color change. Slot in A, BGRA in Value.</summary>
    Color = 0x11,

    /// <summary>A numeric or boolean toolbar setting. <see cref="DemoParam"/> in A, value in B.</summary>
    Param = 0x12,

    /// <summary>An addressable action fired: a CommandRegistry id, or a menu action id.</summary>
    Action = 0x20,

    /// <summary>
    /// An action that opened a modal dialog. Recorded so playback can show what the user did,
    /// but not replayed - the parameters the user typed into the dialog aren't captured by this
    /// format version. See DemoPlayer.
    /// </summary>
    Skipped = 0x21,

    /// <summary>An action plus committed numeric dialog values (format v2 and later).</summary>
    ActionArgs = 0x22,

    /// <summary>Zoom/pan. Cosmetic, but a demo that doesn't follow the user's view is confusing.</summary>
    View = 0x30
}

/// <summary>Which toolbar setting a <see cref="DemoOp.Param"/> event carries. Booleans use 0/1.</summary>
public enum DemoParam : byte
{
    BrushSize = 0,
    Hardness = 1,        // whole percent, as the toolbar talks it
    Tolerance = 2,
    Antialias = 3,
    FillShapes = 4,
    GlobalFill = 5,
    SelectionCombineMode = 6
}

/// <summary>Which color slot a <see cref="DemoOp.Color"/> event sets.</summary>
public enum DemoColorSlot : byte { Foreground = 0, Background = 1 }

/// <summary>
/// A single timestamped input. Kept as a struct with shared slots rather than a class hierarchy:
/// a five-minute session is tens of thousands of these, nearly all of them PointerMove, and the
/// list is walked linearly on playback.
/// </summary>
public readonly struct DemoEvent
{
    public DemoEvent(int timeMs, DemoOp op, float x = 0, float y = 0, float z = 0,
                     int a = 0, int b = 0, uint value = 0, string? text = null,
                     double[]? args = null)
    {
        TimeMs = timeMs;
        Op = op;
        X = x;
        Y = y;
        Z = z;
        A = a;
        B = b;
        Value = value;
        Text = text;
        Args = args;
    }

    /// <summary>Milliseconds since the recording started. Stored on disk as a delta.</summary>
    public int TimeMs { get; }

    public DemoOp Op { get; }

    /// <summary>Image-space X for pointer events; zoom for <see cref="DemoOp.View"/>.</summary>
    public float X { get; }

    /// <summary>Image-space Y for pointer events; view origin X for <see cref="DemoOp.View"/>.</summary>
    public float Y { get; }

    /// <summary>
    /// View origin Y, and nothing else - View is the only opcode carrying three numbers. The
    /// origin is float rather than int because rounding it to whole pixels shifted the replayed
    /// canvas by up to half a pixel, which left the antialiased canvas border differing from the
    /// recording even when every image pixel matched.
    /// </summary>
    public float Z { get; }

    /// <summary>Param kind / color slot / key modifiers.</summary>
    public int A { get; }

    /// <summary>Param value.</summary>
    public int B { get; }

    /// <summary>Packed BGRA for <see cref="DemoOp.Color"/>.</summary>
    public uint Value { get; }

    /// <summary>Tool tag, action id, or marker text.</summary>
    public string? Text { get; }

    /// <summary>Committed dialog values for <see cref="DemoOp.ActionArgs"/>.</summary>
    public double[]? Args { get; }

    public static DemoEvent Down(int t, double x, double y, bool ctrl)
        => new(t, DemoOp.PointerDown, (float)x, (float)y, a: ctrl ? 1 : 0);

    public static DemoEvent Move(int t, double x, double y)
        => new(t, DemoOp.PointerMove, (float)x, (float)y);

    public static DemoEvent Up(int t) => new(t, DemoOp.PointerUp);

    public static DemoEvent Tool(int t, string tag) => new(t, DemoOp.Tool, text: tag);

    public static DemoEvent Color(int t, DemoColorSlot slot, uint bgra)
        => new(t, DemoOp.Color, a: (int)slot, value: bgra);

    public static DemoEvent Param(int t, DemoParam kind, int value)
        => new(t, DemoOp.Param, a: (int)kind, b: value);

    public static DemoEvent Action(int t, string id) => new(t, DemoOp.Action, text: id);

    public static DemoEvent Action(int t, string id, IReadOnlyList<double> args)
        => new(t, DemoOp.ActionArgs, text: id, args: args.ToArray());

    public static DemoEvent Skipped(int t, string label) => new(t, DemoOp.Skipped, text: label);

    public static DemoEvent View(int t, double zoom, double originX, double originY)
        => new(t, DemoOp.View, (float)zoom, (float)originX, (float)originY);
}

