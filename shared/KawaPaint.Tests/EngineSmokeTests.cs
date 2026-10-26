using KawaPaint.Sandbox;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>Runs each Sandbox engine smoke suite as its own test so CI reports which area broke.
/// Suites share %TEMP%/kawa_smoke and process-wide state, so collections run sequentially
/// (see xunit.runner.json).</summary>
public class EngineSmokeTests
{
    [Fact] public void Engine() => EngineSmokeTest.RunAll();
    [Fact] public void Plugins() => PluginSmokeTest.RunAll();
    [Fact] public void PdnPlugins() => PdnPluginSmokeTest.RunAll();
    [Fact] public void Metadata() => MetadataSmokeTest.RunAll();
    [Fact] public void ExportPresets() => ExportPresetSmokeTest.RunAll();
    [Fact] public void DemoFormat() => DemoFormatSmokeTest.RunAll();

    [Fact]
    public void CliPresets()
    {
        // The CLI suite converts the smoke.png written by the engine suite.
        EngineSmokeTest.RunAll();
        CliPresetSmokeTest.RunAll();
    }

    [Fact] public void MailMerge() => MailMergeSmokeTest.RunAll();
    [Fact] public void EngineOptimization() => EngineOptimizationSmokeTest.RunAll();
    [Fact] public void EffectBounds() => EffectBoundsSmokeTest.RunAll();
    [Fact] public void DocumentLifecycle() => DocumentLifecycleSmokeTest.RunAll();
    [Fact] public void ScriptingV2() => ScriptingV2SmokeTest.RunAll();
    [Fact] public void AnimatedGif() => AnimatedGifSmokeTest.RunAll();
    [Fact] public void HistoryTruncate() => HistoryTruncateSmokeTest.RunAll();
    [Fact] public void SelectionAntialias() => SelectionAntialiasSmokeTest.RunAll();
    [Fact] public void ThreeDReference() => ThreeDReferenceSmokeTest.RunAll();
    [Fact] public void TabletInput() => TabletInputSmokeTest.RunAll();
    [Fact] public void PenPath() => PenPathSmokeTest.RunAll();
    [Fact] public void ReferenceModel() => ReferenceModelSmokeTest.RunAll();
    [Fact] public void Publisher() => PublisherSmokeTest.RunAll();
}
