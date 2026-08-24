// KawaPaint - the .kpdemo container: a starting document plus the input stream that acted on it.
//
// Size matters here, so three things are done deliberately:
//   * coordinates are fixed point, delta-encoded against the previous point, and written as zigzag
//     varints - a freehand drag moves a couple of pixels per sample, so a point costs 4-5 bytes
//     instead of the 16 two raw doubles would;
//   * timestamps are varint deltas, so a 16ms pointer cadence costs one byte each;
//   * every repeated string (tool tags, action ids) is written once and referenced by index
//     afterwards, via an inline dictionary that needs no second pass.
// The payload is then gzipped. Measured: ~5 bytes per pointer sample, so 13 seconds of continuous
// drawing is 3.4 KB and an unbroken 5m42s of it is 92 KB - plus whatever the starting document
// costs, which is nothing at all when the canvas started blank. Idle time is free: the recorder
// only ever emits on change, so a demo's size tracks how much was drawn, not how long it ran.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace KawaPaint.App.Core.Demo;

public sealed class DemoFile
{
    public const string Extension = ".kpdemo";

    private static readonly byte[] Magic = { (byte)'K', (byte)'P', (byte)'D', (byte)'E', (byte)'M', (byte)'O', 0 };
    private const byte FormatVersion = 2;

    /// <summary>
    /// Fixed-point denominator for stored coordinates. Not an arbitrary round number: measured
    /// against the real tools, 1/16 px let a coordinate sitting near a rounding boundary flip a
    /// whole pencil pixel (18 pixels differed over a 300-sample drag), while 1/4096 replays every
    /// tool byte-identically. Going finer buys nothing - <see cref="DemoEvent.X"/> is a float, and
    /// at canvas-sized magnitudes its own spacing is already about 1/4096.
    /// </summary>
    private const double CoordScale = 4096.0;

    public string Title { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;

    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }

    /// <summary>
    /// The document as it stood when recording began, in .kwp form (all layers, not a flattened
    /// snapshot). Null means "start from a blank canvas of CanvasWidth x CanvasHeight filled with
    /// <see cref="BlankFill"/>", which is what a demo recorded straight after File > New gets -
    /// and costs nothing.
    /// </summary>
    public byte[]? StartDocument { get; set; }

    /// <summary>BGRA fill for the blank start canvas. Only read when StartDocument is null.</summary>
    public uint BlankFill { get; set; }

    public List<DemoEvent> Events { get; } = new();

    public int DurationMs => Events.Count == 0 ? 0 : Events[^1].TimeMs;

    public int StrokeCount
    {
        get
        {
            int n = 0;
            foreach (var e in Events) if (e.Op == DemoOp.PointerDown) n++;
            return n;
        }
    }

    // ---- write -----------------------------------------------------------

    public void Save(Stream stream)
    {
        stream.Write(Magic, 0, Magic.Length);
        stream.WriteByte(FormatVersion);

        using var gz = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        using var w = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true);

        w.Write(Title);
        w.Write(AppVersion);
        w.Write(RecordedUtc.Ticks);
        w.Write(CanvasWidth);
        w.Write(CanvasHeight);

        if (StartDocument is { Length: > 0 } doc)
        {
            w.Write((byte)1);
            w.Write(doc.Length);
            w.Write(doc);
        }
        else
        {
            w.Write((byte)0);
            w.Write(BlankFill);
        }

        w.Write(Events.Count);

        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        int lastTime = 0, lastFx = 0, lastFy = 0;

        foreach (var e in Events)
        {
            WriteVarUInt(w, (uint)Math.Max(0, e.TimeMs - lastTime));
            lastTime = e.TimeMs;
            w.Write((byte)e.Op);

            switch (e.Op)
            {
                case DemoOp.PointerDown:
                case DemoOp.PointerMove:
                    int fx = Fixed(e.X), fy = Fixed(e.Y);
                    WriteVarInt(w, fx - lastFx);
                    WriteVarInt(w, fy - lastFy);
                    lastFx = fx; lastFy = fy;
                    if (e.Op == DemoOp.PointerDown) w.Write((byte)e.A);
                    break;

                case DemoOp.PointerUp:
                    break;

                case DemoOp.Tool:
                case DemoOp.Action:
                case DemoOp.Skipped:
                    WriteString(w, strings, e.Text ?? "");
                    break;

                case DemoOp.ActionArgs:
                    WriteString(w, strings, e.Text ?? "");
                    var args = e.Args ?? Array.Empty<double>();
                    WriteVarUInt(w, (uint)args.Length);
                    foreach (double arg in args) w.Write(arg);
                    break;

                case DemoOp.Color:
                    w.Write((byte)e.A);
                    w.Write(e.Value);
                    break;

                case DemoOp.Param:
                    w.Write((byte)e.A);
                    WriteVarInt(w, e.B);
                    break;

                case DemoOp.View:
                    w.Write(e.X);   // zoom
                    w.Write(e.Y);   // origin X
                    w.Write(e.Z);   // origin Y
                    break;
            }
        }
    }

    // ---- read ------------------------------------------------------------

    public static DemoFile Load(Stream stream)
    {
        var magic = new byte[Magic.Length];
        if (stream.Read(magic, 0, magic.Length) != magic.Length)
            throw new InvalidDataException("Not a KawaPaint demo file.");
        for (int i = 0; i < Magic.Length; i++)
            if (magic[i] != Magic[i]) throw new InvalidDataException("Not a KawaPaint demo file.");

        int version = stream.ReadByte();
        if (version is not (1 or FormatVersion))
            throw new InvalidDataException(
                $"Demo format version {version} is not supported by this build (expected 1 or {FormatVersion}).");

        var demo = new DemoFile();
        using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var r = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);

        demo.Title = r.ReadString();
        demo.AppVersion = r.ReadString();
        demo.RecordedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
        demo.CanvasWidth = r.ReadInt32();
        demo.CanvasHeight = r.ReadInt32();

        if (r.ReadByte() == 1)
        {
            int len = r.ReadInt32();
            if (len < 0 || len > 512 * 1024 * 1024)
                throw new InvalidDataException("Demo start document length is out of range.");
            demo.StartDocument = r.ReadBytes(len);
        }
        else
        {
            demo.BlankFill = r.ReadUInt32();
        }

        int count = r.ReadInt32();
        if (count < 0) throw new InvalidDataException("Demo event count is negative.");

        var strings = new List<string>();
        int time = 0, fx = 0, fy = 0;

        for (int i = 0; i < count; i++)
        {
            time += (int)ReadVarUInt(r);
            var op = (DemoOp)r.ReadByte();

            switch (op)
            {
                case DemoOp.PointerDown:
                case DemoOp.PointerMove:
                    fx += ReadVarInt(r);
                    fy += ReadVarInt(r);
                    float x = (float)(fx / CoordScale), y = (float)(fy / CoordScale);
                    demo.Events.Add(op == DemoOp.PointerDown
                        ? DemoEvent.Down(time, x, y, r.ReadByte() != 0)
                        : DemoEvent.Move(time, x, y));
                    break;

                case DemoOp.PointerUp:
                    demo.Events.Add(DemoEvent.Up(time));
                    break;

                case DemoOp.Tool:
                    demo.Events.Add(DemoEvent.Tool(time, ReadString(r, strings)));
                    break;

                case DemoOp.Action:
                    demo.Events.Add(DemoEvent.Action(time, ReadString(r, strings)));
                    break;

                case DemoOp.ActionArgs when version >= 2:
                {
                    string id = ReadString(r, strings);
                    uint argCount = ReadVarUInt(r);
                    if (argCount > 64) throw new InvalidDataException("Demo action has too many arguments.");
                    var args = new double[argCount];
                    for (int a = 0; a < args.Length; a++) args[a] = r.ReadDouble();
                    demo.Events.Add(DemoEvent.Action(time, id, args));
                    break;
                }

                case DemoOp.Skipped:
                    demo.Events.Add(DemoEvent.Skipped(time, ReadString(r, strings)));
                    break;

                case DemoOp.Color:
                    demo.Events.Add(DemoEvent.Color(time, (DemoColorSlot)r.ReadByte(), r.ReadUInt32()));
                    break;

                case DemoOp.Param:
                    demo.Events.Add(DemoEvent.Param(time, (DemoParam)r.ReadByte(), ReadVarInt(r)));
                    break;

                case DemoOp.View:
                    demo.Events.Add(DemoEvent.View(time, r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                    break;

                default:
                    throw new InvalidDataException($"Unknown demo opcode 0x{(byte)op:X2} at event {i}.");
            }
        }

        return demo;
    }

    // ---- primitives ------------------------------------------------------

    private static int Fixed(double v) => (int)Math.Round(v * CoordScale);

    private static void WriteVarUInt(BinaryWriter w, uint value)
    {
        while (value >= 0x80) { w.Write((byte)(value | 0x80)); value >>= 7; }
        w.Write((byte)value);
    }

    private static uint ReadVarUInt(BinaryReader r)
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = r.ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Malformed varint in demo stream.");
    }

    // Zigzag, so a small negative delta costs one byte like a small positive one does.
    private static void WriteVarInt(BinaryWriter w, int value)
        => WriteVarUInt(w, (uint)((value << 1) ^ (value >> 31)));

    private static int ReadVarInt(BinaryReader r)
    {
        uint raw = ReadVarUInt(r);
        return (int)(raw >> 1) ^ -(int)(raw & 1);
    }

    /// <summary>
    /// Inline string dictionary: index 0 means "literal follows, and it becomes the next entry",
    /// anything else is a 1-based back-reference. Costs one byte per repeat of a tool tag or
    /// action id, and needs no separate table pass over the events.
    /// </summary>
    private static void WriteString(BinaryWriter w, Dictionary<string, int> table, string s)
    {
        if (table.TryGetValue(s, out int index)) { WriteVarUInt(w, (uint)(index + 1)); return; }
        WriteVarUInt(w, 0);
        w.Write(s);
        table[s] = table.Count;
    }

    private static string ReadString(BinaryReader r, List<string> table)
    {
        uint index = ReadVarUInt(r);
        if (index != 0)
        {
            if (index > table.Count) throw new InvalidDataException("Demo string reference is out of range.");
            return table[(int)index - 1];
        }
        string s = r.ReadString();
        table.Add(s);
        return s;
    }
}
