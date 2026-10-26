using KawaPaint.Sandbox;
using Xunit;

namespace KawaPaint.Tests;

/// <summary>Headless Avalonia regression checks. Avalonia can only be set up once per process,
/// so the whole UI suite is a single test; its failure message lists every failing check.</summary>
public class UiSmokeTests
{
    [Fact] public void HeadlessUi() => UiSmokeTest.RunAll();
}
