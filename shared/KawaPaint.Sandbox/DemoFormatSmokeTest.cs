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

        // Format v2 only adds a new opcode. A v1 stream containing old opcodes must still load.
        var oldDemo = new DemoFile { CanvasWidth = 1, CanvasHeight = 1 };
        oldDemo.Events.Add(DemoEvent.Action(0, "image.flipH"));
        using var oldStream = new MemoryStream();
        oldDemo.Save(oldStream);
        byte[] oldBytes = oldStream.ToArray();
        oldBytes[7] = 1; // seven-byte KPDEMO\0 magic, then the version byte
        using var rewritten = new MemoryStream(oldBytes, writable: false);
        Assert(DemoFile.Load(rewritten).Events.Single().Text == "image.flipH", "v1 compatibility failed");

        Console.WriteLine("DEMO FORMAT SMOKE OK - v2 parameters + v1 compatibility");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Demo format smoke test: " + message);
    }
}
