using KawaPaint.App;
using KawaPaint.App.Core.Demo;

namespace KawaPaint.Sandbox;

internal static class DemoFormatSmokeTest
{
    public static void RunAll()
    {
        var demo = new DemoFile { CanvasWidth = 16, CanvasHeight = 16, BlankFill = 0xFFFFFFFF };
        demo.Events.Add(DemoEvent.Action(10, "effect.bc", new[] { 37.0, 1.42 }));
        using var stream = new MemoryStream();
        demo.Save(stream);
        stream.Position = 0;
        var loaded = DemoFile.Load(stream);
        var action = loaded.Events.Single();
        Assert(action.Op == DemoOp.ActionArgs && action.Text == "effect.bc", "parameterized opcode mismatch");
        Assert(action.Args is { Length: 2 } && action.Args[0] == 37.0 && action.Args[1] == 1.42,
            "committed values did not round-trip");

        var pressureDemo = new DemoFile { CanvasWidth = 8, CanvasHeight = 8 };
        pressureDemo.Events.Add(DemoEvent.Down(1, 2, 3, false, 0.371, ToolPointerKind.Pen,
            isEraser: true, xTilt: -23, yTilt: 41, twist: 178));
        pressureDemo.Events.Add(DemoEvent.Move(2, 4, 5, 0.812, -20, 39, 180));
        using var pressureStream = new MemoryStream();
        pressureDemo.Save(pressureStream);
        pressureStream.Position = 0;
        var pressureEvents = DemoFile.Load(pressureStream).Events;
        Assert(pressureEvents[0].PointerKind == ToolPointerKind.Pen && pressureEvents[0].IsEraser,
            "pen identity did not round-trip");
        Assert(Math.Abs(pressureEvents[0].Pressure - 0.371) < 1.0 / 4095 &&
               pressureEvents[0].XTilt == -23 && pressureEvents[0].YTilt == 41 && pressureEvents[0].Twist == 178,
            "pen pressure/pose did not round-trip");

        // Format v2 only adds a new opcode. A v1 stream containing old opcodes must still load.
        var oldDemo = new DemoFile { CanvasWidth = 1, CanvasHeight = 1 };
        oldDemo.Events.Add(DemoEvent.Action(0, "image.flipH"));
        using var oldStream = new MemoryStream();
        oldDemo.Save(oldStream);
        byte[] oldBytes = oldStream.ToArray();
        oldBytes[7] = 1; // seven-byte KPDEMO\0 magic, then the version byte
        using var rewritten = new MemoryStream(oldBytes, writable: false);
        Assert(DemoFile.Load(rewritten).Events.Single().Text == "image.flipH", "v1 compatibility failed");

        using var legacyPointer = BuildLegacyPointerDemo(version: 2);
        var legacyEvents = DemoFile.Load(legacyPointer).Events;
        Assert(legacyEvents.Count == 3 && legacyEvents[0].Op == DemoOp.PointerDown &&
               legacyEvents[0].Pressure == 1 && legacyEvents[0].PointerKind == ToolPointerKind.Mouse &&
               legacyEvents[1].Op == DemoOp.PointerMove && legacyEvents[2].Op == DemoOp.PointerUp,
            "v2 pointer payload compatibility failed");

        Console.WriteLine("DEMO FORMAT SMOKE OK - v3 pressure + v2 parameters + v1 compatibility");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Demo format smoke test: " + message);
    }

    private static MemoryStream BuildLegacyPointerDemo(byte version)
    {
        var stream = new MemoryStream();
        stream.Write(new byte[] { (byte)'K', (byte)'P', (byte)'D', (byte)'E', (byte)'M', (byte)'O', 0 });
        stream.WriteByte(version);
        using (var gzip = new System.IO.Compression.GZipStream(stream,
                   System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(""); writer.Write(""); writer.Write(0L);
            writer.Write(1); writer.Write(1); writer.Write((byte)0); writer.Write(0u);
            writer.Write(3);
            writer.Write((byte)0); writer.Write((byte)DemoOp.PointerDown);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)1);
            writer.Write((byte)1); writer.Write((byte)DemoOp.PointerMove);
            writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((byte)0); writer.Write((byte)DemoOp.PointerUp);
        }
        stream.Position = 0;
        return stream;
    }
}
