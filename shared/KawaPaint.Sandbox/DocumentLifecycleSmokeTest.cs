using KawaPaint.Engine;

namespace KawaPaint.Sandbox;

internal static class DocumentLifecycleSmokeTest
{
    public static void RunAll()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kawa_cancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "autosave.kwp");
        byte[] existing = [0x4B, 0x41, 0x57, 0x41];
        File.WriteAllBytes(path, existing);

        try
        {
            using var document = new Document(32, 24);
            document.AddLayer("Layer").Surface.Clear(ColorBgra.White);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            bool cancelled = false;
            try { DocumentFile.Save(document, path, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }

            Assert(cancelled, "a cancelled document save did not stop");
            Assert(File.ReadAllBytes(path).SequenceEqual(existing),
                "a cancelled autosave replaced the existing destination");
            Assert(!Directory.EnumerateFiles(directory, ".autosave.kwp.*.tmp").Any(),
                "a cancelled autosave left its atomic-write temp file behind");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }

        Console.WriteLine("DOCUMENT LIFECYCLE SMOKE OK - cancellation preserves destination and cleans temp file");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("DOCUMENT LIFECYCLE SMOKE FAILED: " + message);
    }
}
