if (args.Contains("--ui"))
{
    KawaPaint.Sandbox.UiSmokeTest.RunAll();
    return;
}

// Broad smoke test: touch every engine area once; fail loudly on any exception.
// The same suites run one by one as xUnit tests in shared/KawaPaint.Tests.
KawaPaint.Sandbox.EngineSmokeTest.RunAll();
KawaPaint.Sandbox.PluginSmokeTest.RunAll();
KawaPaint.Sandbox.PdnPluginSmokeTest.RunAll();
KawaPaint.Sandbox.MetadataSmokeTest.RunAll();
KawaPaint.Sandbox.ExportPresetSmokeTest.RunAll();
KawaPaint.Sandbox.DemoFormatSmokeTest.RunAll();
KawaPaint.Sandbox.CliPresetSmokeTest.RunAll();
KawaPaint.Sandbox.MailMergeSmokeTest.RunAll();
KawaPaint.Sandbox.EngineOptimizationSmokeTest.RunAll();
KawaPaint.Sandbox.EffectBoundsSmokeTest.RunAll();
KawaPaint.Sandbox.DocumentLifecycleSmokeTest.RunAll();
KawaPaint.Sandbox.ScriptingV2SmokeTest.RunAll();
KawaPaint.Sandbox.AnimatedGifSmokeTest.RunAll();
KawaPaint.Sandbox.HistoryTruncateSmokeTest.RunAll();
KawaPaint.Sandbox.SelectionAntialiasSmokeTest.RunAll();
KawaPaint.Sandbox.ThreeDReferenceSmokeTest.RunAll();
KawaPaint.Sandbox.TabletInputSmokeTest.RunAll();
KawaPaint.Sandbox.PenPathSmokeTest.RunAll();
KawaPaint.Sandbox.ReferenceModelSmokeTest.RunAll();
KawaPaint.Sandbox.PublisherSmokeTest.RunAll();
