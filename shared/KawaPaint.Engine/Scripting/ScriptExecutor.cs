// KawaPaint - applies a ScriptFile's steps to a Document with no UI, no undo stack, and no
// Avalonia in sight. Mirrors what the interactive handlers in MainView.axaml.cs/MainView.Demo.cs
// do for the same action ids, minus everything that only makes sense for a live session (history
// push, canvas repaint, status text).

namespace KawaPaint.Engine.Scripting;

public enum ScriptStepOutcome { Applied, SkippedNotApplicable, SkippedUnknownId, Failed }

public readonly record struct ScriptStepResult(int StepIndex, string Id, ScriptStepOutcome Outcome, string? Message);

public enum ScriptFailurePolicy { ContinueOnError, StopOnError }

public static class ScriptExecutor
{
    /// <summary>
    /// Applies every step in order, mutating <paramref name="doc"/> in place - except image
    /// transforms that produce a whole new Document (rotate, flatten), which is why this takes the
    /// document by ref: the caller's reference is repointed at the new one and the old one disposed.
    /// A step that can't apply to this particular document (e.g. layer.select.2 on a 1-layer image)
    /// is recorded as SkippedNotApplicable and execution continues - a script is written once and
    /// run over files it wasn't necessarily recorded against, so a mismatch here is an expected
    /// outcome, not a bug. <paramref name="policy"/> only governs whether a step actually throwing
    /// (Failed) stops the rest of this document's steps; a skip never does either way.
    /// </summary>
    public static IReadOnlyList<ScriptStepResult> Run(ref Document doc, ScriptFile script,
        ScriptFailurePolicy policy = ScriptFailurePolicy.ContinueOnError)
    {
        var results = new List<ScriptStepResult>(script.Steps.Count);
        int currentLayer = 0;

        for (int i = 0; i < script.Steps.Count; i++)
        {
            ScriptStep step = script.Steps[i];
            try
            {
                var (outcome, message) = ApplyStep(ref doc, ref currentLayer, step);
                results.Add(new ScriptStepResult(i, step.Id, outcome, message));
                if (outcome == ScriptStepOutcome.Failed && policy == ScriptFailurePolicy.StopOnError) break;
            }
            catch (Exception ex)
            {
                results.Add(new ScriptStepResult(i, step.Id, ScriptStepOutcome.Failed, ex.Message));
                if (policy == ScriptFailurePolicy.StopOnError) break;
            }
        }

        return results;
    }

    private static (ScriptStepOutcome, string?) ApplyStep(ref Document doc, ref int currentLayer, ScriptStep step)
    {
        string id = step.Id;

        switch (id)
        {
            case "image.flipH": DocumentOps.FlipHorizontal(doc); return Ok();
            case "image.flipV": DocumentOps.FlipVertical(doc); return Ok();

            case "image.rotateCW":
            case "image.rotateCCW":
            {
                var rotated = DocumentOps.Rotate90(doc, id == "image.rotateCW");
                doc.Dispose();
                doc = rotated;
                currentLayer = Math.Clamp(currentLayer, 0, doc.LayerCount - 1);
                return Ok();
            }

            case "image.flatten":
                if (doc.LayerCount <= 1) return Skip("already a single layer");
                var flattened = DocumentOps.Flatten(doc);
                doc.Dispose();
                doc = flattened;
                currentLayer = 0;
                return Ok();

            case "layer.add":
                doc.AddLayer();
                currentLayer = doc.LayerCount - 1;
                return Ok();

            case "layer.delete":
                if (doc.LayerCount <= 1) return Skip("can't delete the only layer");
                if (!InRange(currentLayer, doc)) return Skip("no current layer");
                doc.RemoveLayerAt(currentLayer);
                currentLayer = Math.Clamp(currentLayer, 0, doc.LayerCount - 1);
                return Ok();

            case "layer.duplicate":
                if (!InRange(currentLayer, doc)) return Skip("no current layer");
                var dup = doc.Layers[currentLayer].Clone();
                doc.InsertLayer(currentLayer + 1, dup);
                currentLayer++;
                return Ok();

            case "layer.mergeDown":
                if (currentLayer <= 0 || !InRange(currentLayer, doc)) return Skip("nothing below to merge into");
                var above = doc.Layers[currentLayer];
                var below = doc.Layers[currentLayer - 1];
                LayerOps.MergeInto(below, above);
                doc.RemoveLayer(above);
                currentLayer--;
                return Ok();

            case "layer.up":
            case "layer.down":
            {
                if (!InRange(currentLayer, doc)) return Skip("no current layer");
                int to = currentLayer + (id == "layer.up" ? 1 : -1);
                if (to < 0 || to >= doc.LayerCount) return Skip("already at that end");
                doc.MoveLayer(currentLayer, to);
                currentLayer = to;
                return Ok();
            }

            default:
                if (TrySplit(id, "layer.select.", out string arg) && int.TryParse(arg, out int sel))
                {
                    if (!InRange(sel, doc)) return Skip($"no layer {sel} in this document");
                    currentLayer = sel;
                    return Ok();
                }

                if (TrySplit(id, "layer.visible.", out arg))
                {
                    string[] parts = arg.Split('.');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int vi) && InRange(vi, doc))
                    {
                        doc.Layers[vi].Visible = parts[1] == "1";
                        return Ok();
                    }
                    return Skip("layer index out of range");
                }

                if (TrySplit(id, "layer.reorder.", out arg))
                {
                    string[] parts = arg.Split('.');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int from) && int.TryParse(parts[1], out int to2)
                        && InRange(from, doc) && to2 >= 0 && to2 < doc.LayerCount)
                    {
                        doc.MoveLayer(from, to2);
                        if (currentLayer == from) currentLayer = to2;
                        return Ok();
                    }
                    return Skip("layer index out of range");
                }

                if (TrySplit(id, "layer.blend.", out arg))
                {
                    if (!Enum.TryParse<BlendMode>(arg, out var mode)) return Skip("unknown blend mode");
                    if (!InRange(currentLayer, doc)) return Skip("no current layer");
                    doc.Layers[currentLayer].BlendMode = mode;
                    return Ok();
                }

                if (TrySplit(id, "layer.opacity.", out arg))
                {
                    if (!byte.TryParse(arg, out byte opacity)) return Skip("invalid opacity");
                    if (!InRange(currentLayer, doc)) return Skip("no current layer");
                    doc.Layers[currentLayer].Opacity = opacity;
                    return Ok();
                }

                if (TrySplit(id, "effect.", out string tag))
                {
                    if (!InRange(currentLayer, doc)) return Skip("no current layer");
                    var effect = ScriptEffects.Build(tag, step.Args);
                    if (effect is null) return Unknown();
                    effect.Apply(doc.Layers[currentLayer].Surface);
                    return Ok();
                }

                return Unknown();
        }

        static bool InRange(int index, Document d) => index >= 0 && index < d.LayerCount;
        static bool TrySplit(string s, string prefix, out string rest)
        {
            if (s.StartsWith(prefix, StringComparison.Ordinal)) { rest = s[prefix.Length..]; return true; }
            rest = "";
            return false;
        }
        static (ScriptStepOutcome, string?) Ok() => (ScriptStepOutcome.Applied, null);
        static (ScriptStepOutcome, string?) Skip(string why) => (ScriptStepOutcome.SkippedNotApplicable, why);
        static (ScriptStepOutcome, string?) Unknown() => (ScriptStepOutcome.SkippedUnknownId, null);
    }
}
