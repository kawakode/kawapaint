// KawaPaint - the command-line batch-apply entry point. Lives in its own project (referencing only
// KawaPaint.Engine, no Avalonia) so win\Program.cs and linux\Program.cs share one argument parser
// instead of each growing their own, and so the Engine itself stays free of console/CLI concerns.
//
// Exit codes: 0 every target opened, ran, and saved with no skipped/failed steps; 1 at least one
// target failed to open or save; 2 everything opened/saved but some steps were skipped or failed
// along the way. Worth getting this right from the start - a CI script that greps stdout to tell
// "broken" from "ran with warnings" apart is exactly the kind of thing that's painful to fix later
// without breaking someone's existing automation.

using KawaPaint.Engine.Scripting;
using KawaPaint.Engine.Exporting;
using System.Text.Json;

namespace KawaPaint.Cli;

public static class BatchCliRunner
{
    public static bool IsBatchInvocation(string[] args)
        => Array.IndexOf(args, "--script") >= 0 || Array.IndexOf(args, "--preset") >= 0;

    public static int Run(string[] args)
    {
        Options opts;
        try
        {
            opts = Options.Parse(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("kawapaint: " + ex.Message);
            Console.Error.WriteLine(Usage);
            return 1;
        }

        if (opts.PresetName is not null) return RunPreset(opts);

        ScriptFile script;
        try { script = ScriptFile.Load(opts.ScriptPath!); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"kawapaint: could not read script '{opts.ScriptPath}': {ex.Message}");
            return 1;
        }

        List<(string In, string Out)> targets;
        try
        {
            targets = ResolveTargets(opts);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("kawapaint: " + ex.Message);
            return 1;
        }

        if (targets.Count == 0)
        {
            Console.Error.WriteLine("kawapaint: no input files matched.");
            return 1;
        }

        var policy = opts.StopOnError ? ScriptFailurePolicy.StopOnError : ScriptFailurePolicy.ContinueOnError;
        var results = BatchRunner.RunMany(script, targets, policy);

        bool anyOpenSaveFailure = false;
        bool anyStepIssue = false;

        foreach (var r in results)
        {
            if (!r.Opened || !r.Saved)
            {
                anyOpenSaveFailure = true;
                Console.WriteLine($"FAIL  {r.InputPath}: {r.Error}");
                continue;
            }

            var issues = r.Steps.Where(s => s.Outcome is ScriptStepOutcome.SkippedNotApplicable
                or ScriptStepOutcome.SkippedUnknownId or ScriptStepOutcome.Failed).ToList();
            if (issues.Count > 0)
            {
                anyStepIssue = true;
                Console.WriteLine($"WARN  {r.InputPath} -> {r.OutputPath}  ({issues.Count} step(s) skipped/failed)");
                foreach (var s in issues)
                    Console.WriteLine($"        step {s.StepIndex} '{s.Id}': {s.Outcome}" + (s.Message is null ? "" : " - " + s.Message));
            }
            else
            {
                Console.WriteLine($"OK    {r.InputPath} -> {r.OutputPath}  ({r.Steps.Count} step(s))");
            }
        }

        Console.WriteLine($"{results.Count} file(s): {results.Count(r => r.Opened && r.Saved)} saved, {results.Count(r => !r.Opened || !r.Saved)} failed.");

        return anyOpenSaveFailure ? 1 : anyStepIssue ? 2 : 0;
    }

    private static int RunPreset(Options opts)
    {
        string settingsPath = opts.SettingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KawaPaint", "settings.json");
        ExportPreset preset;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!json.RootElement.TryGetProperty("ExportPresets", out var node))
                throw new InvalidDataException("settings contain no export presets");
            var presets = node.Deserialize<Dictionary<string, ExportPreset>>(new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true }) ?? new();
            if (!presets.TryGetValue(opts.PresetName!, out preset!))
                throw new InvalidDataException($"preset '{opts.PresetName}' was not found");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"kawapaint: could not load preset from '{settingsPath}': {ex.Message}");
            return 1;
        }

        if (opts.ScriptPath is not null) preset.ScriptPath = opts.ScriptPath;
        string settingsDir = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        string? configuredFolder = opts.OutDir ?? preset.OutputFolder;
        if (configuredFolder is not null && !Path.IsPathRooted(configuredFolder))
            configuredFolder = Path.GetFullPath(Path.Combine(settingsDir, configuredFolder));

        List<string> inputs;
        try { inputs = ResolveInputs(opts); }
        catch (UsageException ex)
        {
            Console.Error.WriteLine("kawapaint: " + ex.Message);
            return 1;
        }

        bool anyFailure = false, anyStepIssue = false;
        foreach (string input in inputs)
        {
            try
            {
                string? folder = opts.InPlace ? Path.GetDirectoryName(Path.GetFullPath(input)) : configuredFolder;
                if (folder is null) throw new UsageException("the preset has no output folder; pass --out-dir");
                var result = PresetExporter.ExportInputFile(input, opts.PresetName!, preset, folder, settingsDir);
                int issues = result.ScriptSteps.Count(s => s.Outcome != ScriptStepOutcome.Applied);
                anyStepIssue |= issues > 0;
                Console.WriteLine((issues == 0 ? "OK    " : "WARN  ") +
                    $"{input} -> {result.OutputPath} ({result.Width}x{result.Height}" +
                    (result.SidecarPath is null ? "" : ", caption") + (issues == 0 ? ")" : $", {issues} script issue(s))"));
            }
            catch (Exception ex)
            {
                anyFailure = true;
                Console.WriteLine($"FAIL  {input}: {ex.Message}");
                if (opts.StopOnError) break;
            }
        }
        return anyFailure ? 1 : anyStepIssue ? 2 : 0;
    }

    private static List<(string In, string Out)> ResolveTargets(Options opts)
    {
        var inputs = ResolveInputs(opts);

        var targets = new List<(string, string)>(inputs.Count);
        foreach (string input in inputs)
        {
            string outputPath = opts.InPlace
                ? input
                : Path.Combine(opts.OutDir!, Path.GetFileName(input));
            targets.Add((input, outputPath));
        }
        return targets;
    }

    private static List<string> ResolveInputs(Options opts)
    {
        var inputs = new List<string>();
        inputs.AddRange(opts.InFiles);
        if (opts.InDir is not null)
        {
            string pattern = opts.Pattern ?? "*";
            if (!Directory.Exists(opts.InDir)) throw new UsageException($"--in-dir directory not found: {opts.InDir}");
            inputs.AddRange(Directory.GetFiles(opts.InDir, pattern));
        }
        if (opts.InList is not null)
        {
            if (!File.Exists(opts.InList)) throw new UsageException($"--in-list file not found: {opts.InList}");
            inputs.AddRange(File.ReadAllLines(opts.InList).Select(l => l.Trim()).Where(l => l.Length > 0));
        }
        if (inputs.Count == 0) throw new UsageException("no input files matched");
        return inputs;
    }

    private const string Usage =
        "usage: kawapaint (--script <path.kpscript> | --preset <name> [--settings <settings.json>]) " +
        "(--in <file>... | --in-dir <dir> [--pattern <glob>] | --in-list <file>) " +
        "[--out-dir <dir> | --in-place] [--stop-on-error]";

    private sealed class Options
    {
        public string? ScriptPath;
        public string? PresetName;
        public string? SettingsPath;
        public List<string> InFiles = new();
        public string? InDir;
        public string? Pattern;
        public string? InList;
        public string? OutDir;
        public bool InPlace;
        public bool StopOnError;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--script": o.ScriptPath = Next(args, ref i, "--script"); break;
                    case "--preset": o.PresetName = Next(args, ref i, "--preset"); break;
                    case "--settings": o.SettingsPath = Next(args, ref i, "--settings"); break;
                    case "--in":
                        while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                            o.InFiles.Add(args[++i]);
                        break;
                    case "--in-dir": o.InDir = Next(args, ref i, "--in-dir"); break;
                    case "--pattern": o.Pattern = Next(args, ref i, "--pattern"); break;
                    case "--in-list": o.InList = Next(args, ref i, "--in-list"); break;
                    case "--out-dir": o.OutDir = Next(args, ref i, "--out-dir"); break;
                    case "--in-place": o.InPlace = true; break;
                    case "--stop-on-error": o.StopOnError = true; break;
                    default: throw new UsageException($"unrecognized argument '{args[i]}'");
                }
            }

            if (o.ScriptPath is null && o.PresetName is null)
                throw new UsageException("one of --script or --preset is required");
            if (o.InFiles.Count == 0 && o.InDir is null && o.InList is null)
                throw new UsageException("at least one of --in, --in-dir, --in-list is required");
            if (o.PresetName is null && o.OutDir is null && !o.InPlace)
                throw new UsageException("one of --out-dir or --in-place is required");
            if (o.OutDir is not null && o.InPlace) throw new UsageException("--out-dir and --in-place are mutually exclusive");

            return o;
        }

        private static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length) throw new UsageException($"{flag} needs a value");
            return args[++i];
        }
    }

    private sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }
}
