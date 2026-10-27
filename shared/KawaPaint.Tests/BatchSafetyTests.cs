using KawaPaint.Engine;
using KawaPaint.Engine.Exporting;
using KawaPaint.Engine.MailMerge;
using Xunit;

namespace KawaPaint.Tests;

public class BatchSafetyTests
{
    [Fact]
    public void MemoryEstimateCountsLayersAndWorkingSurfaces()
    {
        // 20000 x 20000 is 1.6 GB per surface; one layer plus three working surfaces.
        Assert.Equal(20000L * 20000 * 4 * 4, ImageMemory.Estimate(20000, 20000, 1));
        Assert.Equal(100L * 100 * 4 * 5 + 7, ImageMemory.Estimate(100, 100, 2, previousBytes: 7));
    }

    [Theory]
    [InlineData(400, ImageMemoryFit.Comfortable)]
    [InlineData(600, ImageMemoryFit.Tight)]
    [InlineData(950, ImageMemoryFit.TooLarge)]
    public void MemoryCheckWarnsThenRefuses(long bytes, ImageMemoryFit expected) =>
        Assert.Equal(expected, ImageMemory.Check(bytes, budget: 1000));

    [Fact]
    public void MailMergeStopsWhenCancelledAndReportsProgress()
    {
        string dir = Directory.CreateTempSubdirectory("kawapaint-merge-").FullName;
        try
        {
            using var template = new Document(32, 16);
            template.AddLayer();
            template.DynamicTextZones.Add(new DynamicTextZone { Name = "n", Template = "{Name}", X = 0, Y = 0, Width = 32, Height = 16 });
            var data = CsvData.Parse("Name\nA\nB\nC\nD");
            var preset = new ExportPreset { CodecId = "png" };
            using var cancel = new CancellationTokenSource();
            var seen = new List<int>();

            var results = MailMergeRunner.Run(template, data, "t.kwp", "p", preset, dir, "{row}.{ext}",
                progress: (done, row) => { seen.Add(row); if (row == 2) cancel.Cancel(); },
                cancellationToken: cancel.Token);

            // Row 2 was already under way when Cancel came, so it finishes; rows 3 and 4 never start.
            Assert.Equal(new[] { 1, 2 }, seen);
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Null(r.Error));
            Assert.Equal(new[] { "1.png", "2.png" }, Directory.GetFiles(dir).Select(Path.GetFileName).Order());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
