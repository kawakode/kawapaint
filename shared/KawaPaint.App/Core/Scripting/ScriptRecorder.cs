// KawaPaint - collects the step list a ScriptFile is saved from. Much smaller than DemoRecorder:
// a script only ever carries actions (effects, image transforms, layer ops), never pointer/tool/
// color/param/view events, so there's no redundancy filtering or timing to track - just an
// allow-list deciding which action ids are worth keeping at all. See MainView.Script.cs for how
// recording is wired into the editor, and shared\KawaPaint.Engine\Scripting\ScriptEffects.cs for
// the matching set of effect tags.

using System;
using System.Collections.Generic;
using KawaPaint.Engine.Scripting;

namespace KawaPaint.App.Core.Scripting;

public sealed class ScriptRecorder
{
    private ScriptFile? _script;
    private int _suppress;

    public bool IsRecording => _script is not null;
    public int StepCount => _script?.Steps.Count ?? 0;

    /// <summary>Raised after every recorded step, so the UI can show a live counter.</summary>
    public event Action? Progress;

    public void Start(string appVersion)
    {
        _script = new ScriptFile { AppVersion = appVersion, RecordedUtc = DateTime.UtcNow };
        _suppress = 0;
    }

    public ScriptFile? Stop()
    {
        if (_script is null) return null;
        var done = _script;
        _script = null;
        return done;
    }

    /// <summary>
    /// Opens a scope in which notes are ignored - wraps anything that dispatches through a second
    /// recorded path (a registry command running its menu handler), same purpose as
    /// <see cref="Demo.DemoRecorder.Suppress"/> but its own independent counter, since a demo
    /// recording and a script recording never run at the same time but are still two separate taps
    /// on the same CommandRegistry.DispatchScope hook.
    /// </summary>
    public IDisposable Suppress() => new Scope(this);

    private sealed class Scope : IDisposable
    {
        private readonly ScriptRecorder _owner;
        private bool _done;
        public Scope(ScriptRecorder owner) { _owner = owner; owner._suppress++; }
        public void Dispose() { if (_done) return; _done = true; _owner._suppress--; }
    }

    private bool Off => _script is null || _suppress > 0;

    public void NoteAction(string id)
    {
        if (Off || !IsScriptable(id)) return;
        _script!.Steps.Add(new ScriptStep(id));
        Progress?.Invoke();
    }

    public void NoteAction(string id, IReadOnlyList<double> args)
        => NoteAction(id, args, null);

    public void NoteAction(string id, IReadOnlyList<double> args, IReadOnlyList<string>? stringArgs)
    {
        if (Off || !IsScriptable(id)) return;
        _script!.Steps.Add(new ScriptStep(id, args, stringArgs));
        Progress?.Invoke();
    }

    /// <summary>
    /// The allow-list of ids a script can carry. Deliberately an allow-list rather than a deny-
    /// list: a future command (a new tool, a plugin-contributed one) is inert in scripts by default
    /// and only becomes scriptable once someone deliberately teaches this method about it, instead
    /// of silently becoming scriptable and needing to be remembered as an exclusion.
    ///
    /// Excluded on purpose: image.crop (needs a live selection, no headless equivalent),
    /// effect.clouds (its factory reads the live foreground/background color - see ScriptEffects),
    /// and everything colour/undo-stack/viewport/selection/clipboard/file related, none of which a
    /// headless target document has.
    /// </summary>
    public static bool IsScriptable(string id)
    {
        switch (id)
        {
            case "image.flipH":
            case "image.flipV":
            case "image.rotateCW":
            case "image.rotateCCW":
            case "image.flatten":
            case "layer.add":
            case "layer.delete":
            case "layer.duplicate":
            case "layer.mergeDown":
            case "layer.rename":
            case "text.draw":
            case "layer.up":
            case "layer.down":
                return true;
        }

        if (TrySplit(id, "layer.select.", out _)) return true;
        if (TrySplit(id, "layer.visible.", out _)) return true;
        if (TrySplit(id, "layer.reorder.", out _)) return true;
        if (TrySplit(id, "layer.blend.", out _)) return true;
        if (TrySplit(id, "layer.opacity.", out _)) return true;
        if (TrySplit(id, "effect.", out string tag)) return ScriptEffects.IsKnownTag(tag);

        return false;
    }

    private static bool TrySplit(string id, string prefix, out string rest)
    {
        if (id.StartsWith(prefix, StringComparison.Ordinal)) { rest = id[prefix.Length..]; return true; }
        rest = "";
        return false;
    }
}
