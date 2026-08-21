// KawaPaint - runs a script over one or more files on disk. This is the filesystem-path
// convenience path used by the CLI (which only ever has argv paths to work with) and, where a
// desktop file picker resolves to a real local path, by the GUI too - so the two never apply a
// script differently. Decode -> ScriptExecutor.Run -> encode is the whole job; everything that
// knows what an effect or a layer op *is* lives in ScriptExecutor/ScriptEffects, not here.

using KawaPaint.Engine.Codecs;

namespace KawaPaint.Engine.Scripting;

public sealed record BatchFileResult(
    string InputPath, string OutputPath, bool Opened, bool Saved,
    IReadOnlyList<ScriptStepResult> Steps, string? Error);

public static class BatchRunner
{
    /// <summary>
    /// Runs one file. Input is read fully into memory and closed before output is touched, so
    /// InputPath == OutputPath (in-place overwrite) is safe - and the write itself goes through a
    /// temp-file-then-move, matching <see cref="DocumentFile.Save(Document,string)"/>'s crash
    /// safety, so a failure partway through never leaves a truncated file behind either way.
    /// </summary>
    public static BatchFileResult RunOne(ScriptFile script, string inputPath, string outputPath,
        ScriptFailurePolicy policy = ScriptFailurePolicy.ContinueOnError)
    {
        Document? doc = null;
        try
        {
            byte[] bytes = File.ReadAllBytes(inputPath);
            using (var input = new MemoryStream(bytes, writable: false))
                doc = Open(input, inputPath);

            var results = ScriptExecutor.Run(ref doc, script, policy);

            SaveAtomic(doc, outputPath);
            return new BatchFileResult(inputPath, outputPath, true, true, results, null);
        }
        catch (Exception ex)
        {
            return new BatchFileResult(inputPath, outputPath, doc is not null, false,
                Array.Empty<ScriptStepResult>(), ex.Message);
        }
        finally
        {
            doc?.Dispose();
        }
    }

    public static IReadOnlyList<BatchFileResult> RunMany(ScriptFile script,
        IReadOnlyList<(string In, string Out)> targets, ScriptFailurePolicy policy = ScriptFailurePolicy.ContinueOnError)
    {
        var results = new List<BatchFileResult>(targets.Count);
        foreach (var (inPath, outPath) in targets)
            results.Add(RunOne(script, inPath, outPath, policy));
        return results;
    }

    private static Document Open(Stream input, string fileName)
        => fileName.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase)
            ? DocumentFile.Load(input)
            : WrapSurface(CodecRegistry.Decode(input, fileName));

    private static Document WrapSurface(Surface surface)
    {
        var doc = new Document(surface.Width, surface.Height);
        doc.AddLayer(new Layer(surface));
        return doc;
    }

    private static void SaveAtomic(Document doc, string outputPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) is { Length: > 0 } d ? d : ".";
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Create(temp))
            {
                if (outputPath.EndsWith(DocumentFile.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    DocumentFile.Save(doc, file);
                }
                else
                {
                    using Surface flat = doc.Flatten();
                    CodecRegistry.Encode(flat, file, outputPath);
                }
            }
            File.Move(temp, outputPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
