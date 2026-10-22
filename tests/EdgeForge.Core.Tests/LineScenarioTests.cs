using System.Text.Json;
using EdgeForge.Decision;
using EdgeForge.Runtime;
using EdgeForge.Vision;
using OpenCvSharp;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The line end to end, minus the camera: frames drawn with eggs of known size
/// moving along a belt, through the real scenario, pipeline, detector, tracker,
/// rule and journal. What the station would say about a batch we made ourselves.
/// </summary>
public sealed class LineScenarioTests : IDisposable
{
    private const double MmPerPixel = 1.0 / 3.0;
    private const int Width = 960;
    private const int Height = 400;
    private const double Fps = 25;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "edgeforge-line-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private EdgeForgeOptions Options() => new()
    {
        Scenario = ScenarioNames.Line,
        Detector = new DetectorOptions { Kind = DetectorKinds.Blob },
        Blob = new BlobOptions
        {
            ClassName = "egg",
            MmPerPixel = MmPerPixel,
            MinAreaFraction = 0.005,
            MaxAreaFraction = 0.2
        },
        Line = new LineOptions
        {
            DecisionLine = 0.5,
            MaxJump = 0.1,
            Limits = [new LimitOptions { Measurement = "width_mm", Min = 42, Reason = "Undersize" }],
            JournalPath = Path.Combine(_directory, "items.jsonl")
        }
    };

    private sealed class Silent : IInspectionAction
    {
        public Task ExecuteAsync(InspectionResult result, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// A belt moving at 120 mm/s carrying eggs 100 mm apart, each with its own
    /// width. Returns one frame per 1/25 s until the last egg has left.
    /// </summary>
    private static IEnumerable<Frame> Conveyor(IReadOnlyList<double> widthsMm, DateTimeOffset origin)
    {
        const double speedPxPerFrame = 120 / MmPerPixel / Fps;
        const double spacingPx = 100 / MmPerPixel;

        var totalTravel = Width + spacingPx * widthsMm.Count + 200;
        var frames = (int)(totalTravel / speedPxPerFrame);

        for (var f = 0; f < frames; f++)
        {
            var mat = new Mat(new Size(Width, Height), MatType.CV_8UC3, new Scalar(45, 42, 40));

            for (var i = 0; i < widthsMm.Count; i++)
            {
                var x = f * speedPxPerFrame - 100 - i * spacingPx;
                if (x < -150 || x > Width + 150) continue;

                var width = widthsMm[i];
                Cv2.Ellipse(
                    mat,
                    new RotatedRect(
                        new Point2f((float)x, Height / 2f),
                        new Size2f((float)(width * 1.3 / MmPerPixel), (float)(width / MmPerPixel)),
                        0),
                    new Scalar(170, 200, 225),
                    thickness: -1);
            }

            yield return new Frame($"SYN-{f:0000}", DateTimeOffset.UtcNow, Width, Height, new MatFrameImage(mat))
            {
                SceneTime = origin + TimeSpan.FromSeconds(f / Fps)
            };
        }
    }

    private async Task<(Scenario Scenario, LineEventSink Sink)> RunBatchAsync(IReadOnlyList<double> widths)
    {
        var scenario = Scenario.Create(Options(), new Silent(), _directory);
        var pipeline = new InspectionPipeline(scenario.Detector, scenario.Inspector, scenario.Action);

        foreach (var frame in Conveyor(widths, new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero)))
        {
            try
            {
                await pipeline.ProcessAsync(frame);
            }
            finally
            {
                frame.Image?.Dispose();
            }
        }

        return (scenario, Assert.IsType<LineEventSink>(scenario.Sink));
    }

    [Fact]
    public async Task ABatchOfEggs_IsCountedOncePerEgg_AndTheSmallOnesRejected()
    {
        double[] widths = [44, 45, 39, 43, 40, 46, 44, 38];

        var (scenario, sink) = await RunBatchAsync(widths);
        using var _ = scenario;

        var snapshot = sink.Snapshot;
        Assert.Equal(widths.Length, snapshot.Judged);
        Assert.Equal(widths.Count(w => w < 42), snapshot.Nok);
        Assert.Equal(widths.Count(w => w >= 42), snapshot.Ok);
        Assert.Equal(0, snapshot.Unknown);
        Assert.Equal(widths.Count(w => w < 42), snapshot.NokReasons["Undersize"]);
    }

    [Fact]
    public async Task ThroughputIsTheBeltsNotTheDecoders()
    {
        // 120 mm/s with 100 mm spacing is 72 eggs a minute, whatever the speed at
        // which the frames were processed - the sink runs on scene time.
        var (scenario, sink) = await RunBatchAsync([44, 44, 44, 44, 44, 44]);
        using var _ = scenario;

        Assert.Equal(72, sink.Snapshot.PerMinute!.Value, tolerance: 2);
    }

    [Fact]
    public async Task EveryVerdictReachesTheJournal_WithItsMeasurement()
    {
        double[] widths = [44, 39];

        var (scenario, _) = await RunBatchAsync(widths);
        using var __ = scenario;

        var lines = File.ReadAllLines(Path.Combine(_directory, "items.jsonl"));
        Assert.Equal(2, lines.Length);

        for (var i = 0; i < widths.Length; i++)
        {
            using var json = JsonDocument.Parse(lines[i]);
            var root = json.RootElement;

            Assert.Equal(widths[i] < 42 ? "NOK" : "OK", root.GetProperty("Status").GetString());
            Assert.Equal(widths[i], root.GetProperty("Measurements").GetProperty("width_mm").GetDouble(), tolerance: 0.7);
        }
    }

    [Fact]
    public void TheBowlsScenario_StillBuildsTheMealPipeline()
    {
        var options = new EdgeForgeOptions
        {
            Meals = new MealsOptions
            {
                JournalPath = Path.Combine(_directory, "meals.jsonl"),
                StatsPath = Path.Combine(_directory, "stats.json")
            }
        };

        using var scenario = Scenario.Create(options, new Silent(), _directory);

        Assert.Equal(ScenarioNames.Bowls, scenario.Name);
        Assert.IsType<SampleDetector>(scenario.Detector);
        Assert.IsType<MealEventSink>(scenario.Sink);
        Assert.Null(scenario.Rule);
    }

    [Fact]
    public void TheBowlsWithABlobDetector_GetPresenceOfTheBlobClass()
    {
        var options = new EdgeForgeOptions
        {
            Detector = new DetectorOptions { Kind = DetectorKinds.Blob },
            Meals = new MealsOptions { Enabled = false }
        };

        using var scenario = Scenario.Create(options, new Silent(), _directory);

        Assert.IsType<BlobDetector>(scenario.Detector);
        Assert.IsType<PresenceInspector>(scenario.Inspector);
    }

    [Fact]
    public async Task ThePipelineStampsTheResultWithSceneTime()
    {
        using var scenario = Scenario.Create(Options(), new Silent(), _directory);
        var pipeline = new InspectionPipeline(scenario.Detector, scenario.Inspector, scenario.Action);
        var scene = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var (result, _) = await pipeline.ProcessAsync(
            new Frame("F", DateTimeOffset.UtcNow, 640, 360) { SceneTime = scene });

        Assert.Equal(scene, result.Timestamp);
    }

    [Fact]
    public async Task WithoutSceneTime_TheResultCarriesTheCaptureTimestamp()
    {
        using var scenario = Scenario.Create(Options(), new Silent(), _directory);
        var pipeline = new InspectionPipeline(scenario.Detector, scenario.Inspector, scenario.Action);
        var captured = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

        var (result, _) = await pipeline.ProcessAsync(new Frame("F", captured, 640, 360));

        Assert.Equal(captured, result.Timestamp);
    }
}
