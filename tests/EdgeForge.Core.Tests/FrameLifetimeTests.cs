using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// Guards the frame-ownership rule: the consumer owns a frame after read and must
/// dispose it, and anything left queued at shutdown must still be released.
/// Uses a counting fake, so these run with no camera and no OpenCV involved.
/// </summary>
public class FrameLifetimeTests
{
    private sealed class FakeFrameImage : IFrameImage
    {
        public int DisposeCount { get; private set; }
        public int Width => 640;
        public int Height => 480;
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeFrameSource : IFrameSource
    {
        private readonly IReadOnlyList<Frame> _frames;

        public FakeFrameSource(IReadOnlyList<Frame> frames) => _frames = frames;

        public int Yielded { get; private set; }

        public async IAsyncEnumerable<Frame> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var index = 0;
            try
            {
                for (; index < _frames.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Yielded++;
                    yield return _frames[index];
                    await Task.Yield();
                }
            }
            finally
            {
                // Drain: whatever was never handed out is still ours to release.
                for (var i = index + 1; i < _frames.Count; i++)
                {
                    _frames[i].Image?.Dispose();
                }
            }
        }
    }

    private static Frame CreateFrame(int i, FakeFrameImage image) =>
        new($"CAM-{i:000000}", DateTimeOffset.UtcNow, image.Width, image.Height, image);

    [Fact]
    public void DisposingTwiceStillReleasesOnce()
    {
        var mat = new OpenCvSharp.Mat(4, 4, OpenCvSharp.MatType.CV_8UC3);
        var image = new EdgeForge.Vision.MatFrameImage(mat);

        image.Dispose();
        image.Dispose();

        Assert.True(mat.IsDisposed);
    }

    [Fact]
    public async Task ConsumerDisposesEveryFrameItReads()
    {
        var images = Enumerable.Range(0, 5).Select(_ => new FakeFrameImage()).ToList();
        var frames = images.Select((img, i) => CreateFrame(i, img)).ToList();
        var source = new FakeFrameSource(frames);

        await foreach (var frame in source.ReadFramesAsync())
        {
            try { /* inspection happens here */ }
            finally { frame.Image?.Dispose(); }
        }

        Assert.All(images, img => Assert.Equal(1, img.DisposeCount));
    }

    [Fact]
    public async Task BreakingEarlyStillReleasesQueuedFrames()
    {
        var images = Enumerable.Range(0, 5).Select(_ => new FakeFrameImage()).ToList();
        var frames = images.Select((img, i) => CreateFrame(i, img)).ToList();
        var source = new FakeFrameSource(frames);

        await foreach (var frame in source.ReadFramesAsync())
        {
            frame.Image?.Dispose();
            break;
        }

        // This is the assertion that would have caught a DropOldest leak:
        // nothing may survive the loop undisposed.
        Assert.All(images, img => Assert.True(img.DisposeCount >= 1));
        Assert.DoesNotContain(images, img => img.DisposeCount == 0);
    }
}
