using EdgeForge.Runtime;
using Xunit;

namespace EdgeForge.Core.Tests;

public class InspectionPipelineTests
{
    private sealed class FakeDetector : IVisionDetector
    {
        private readonly IReadOnlyList<Detection> _detections;

        public FakeDetector(params string[] classNames) =>
            _detections = classNames
                .Select(n => new Detection(n, 0.9, new BoundingBox(0, 0, 1, 1)))
                .ToList();

        public Task<IReadOnlyList<Detection>> DetectAsync(
            Frame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_detections);
        }
    }

    private sealed class RecordingAction : IInspectionAction
    {
        public int Invocations { get; private set; }

        public Task ExecuteAsync(
            InspectionResult result, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations++;
            return Task.CompletedTask;
        }
    }

    private static Frame CreateFrame() =>
        new("TEST-001", DateTimeOffset.UtcNow, 1920, 1080);

    private static InspectionPipeline CreatePipeline(
        IVisionDetector detector, IInspectionAction action) =>
        new(detector, new EdgeForge.Vision.LabelInspector(), action);

    [Theory]
    [InlineData("correct_label", InspectionStatus.Ok)]
    [InlineData("wrong_label", InspectionStatus.Nok)]
    public async Task DetectedLabelDrivesStatus(string className, InspectionStatus expected)
    {
        var pipeline = CreatePipeline(new FakeDetector(className), new RecordingAction());

        var (result, _) = await pipeline.ProcessAsync(CreateFrame());

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task NoDetectionIsUnknownNotOk()
    {
        var pipeline = CreatePipeline(new FakeDetector(), new RecordingAction());

        var (result, _) = await pipeline.ProcessAsync(CreateFrame());

        Assert.Equal(InspectionStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task ActionRunsExactlyOncePerFrame()
    {
        var action = new RecordingAction();
        var pipeline = CreatePipeline(new FakeDetector("correct_label"), action);

        await pipeline.ProcessAsync(CreateFrame());
        await pipeline.ProcessAsync(CreateFrame());

        Assert.Equal(2, action.Invocations);
    }

    [Fact]
    public async Task TotalLatencyCoversInference()
    {
        var pipeline = CreatePipeline(new FakeDetector("correct_label"), new RecordingAction());

        var (_, metrics) = await pipeline.ProcessAsync(CreateFrame());

        // An invariant, not a magic-number timing assert: those are flaky.
        Assert.True(metrics.TotalLatency >= metrics.InferenceLatency);
        Assert.True(metrics.CaptureLatency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task CancellationSkipsTheAction()
    {
        var action = new RecordingAction();
        var pipeline = CreatePipeline(new FakeDetector("correct_label"), action);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.ProcessAsync(CreateFrame(), cts.Token));

        // Fail safe: a cancelled inspection must not trigger a physical action.
        Assert.Equal(0, action.Invocations);
    }
}
