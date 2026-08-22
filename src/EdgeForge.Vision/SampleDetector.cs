using EdgeForge.Core;

namespace EdgeForge.Vision;

public sealed class SampleDetector : IVisionDetector
{
    public Task<IReadOnlyList<Detection>> DetectAsync(
        Frame frame,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var detections = frame.Id == "BOX-003"
            ? new[]
            {
                new Detection(
                    "wrong_label",
                    0.96,
                    new BoundingBox(0.25, 0.30, 0.50, 0.20))
            }
            : new[]
            {
                new Detection(
                    "correct_label",
                    0.98,
                    new BoundingBox(0.25, 0.30, 0.50, 0.20))
            };

        return Task.FromResult<IReadOnlyList<Detection>>(detections);
    }
}
