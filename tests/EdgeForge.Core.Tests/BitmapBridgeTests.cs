using EdgeForge.Core;
using EdgeForge.Vision;
using OpenCvSharp;
using Xunit;

namespace EdgeForge.Core.Tests;

public class BitmapBridgeTests
{
    private static Frame FrameWith(Mat mat) =>
        new("TEST-001", DateTimeOffset.UtcNow, mat.Width, mat.Height, new MatFrameImage(mat));

    [Fact]
    public void FrameWithoutImage_IsNotAFailure()
    {
        // Synthetic frames carry no pixels. The viewer must show the stats and skip
        // the bitmap, exactly as OnnxVisionDetector returns no detections.
        var frame = new Frame("SYN-001", DateTimeOffset.UtcNow, 1920, 1080);

        Assert.Equal(0, BitmapBridge.RequiredBufferSize(frame));
        Assert.False(BitmapBridge.TryCopyBgra(frame, new byte[16], out _, out _));
    }

    [Fact]
    public void RequiredBufferSize_IsFourBytesPerPixel()
    {
        using var mat = new Mat(3, 5, MatType.CV_8UC3, Scalar.All(0));

        Assert.Equal(3 * 5 * 4, BitmapBridge.RequiredBufferSize(FrameWith(mat)));
    }

    [Fact]
    public void CopyBgra_PreservesChannelOrderAndAddsOpaqueAlpha()
    {
        using var mat = new Mat(1, 2, MatType.CV_8UC3);
        mat.Set(0, 0, new Vec3b(10, 20, 30));   // OpenCV stores BGR
        mat.Set(0, 1, new Vec3b(40, 50, 60));

        var buffer = new byte[BitmapBridge.RequiredBufferSize(FrameWith(mat))];
        var copied = BitmapBridge.TryCopyBgra(FrameWith(mat), buffer, out var width, out var height);

        Assert.True(copied);
        Assert.Equal(2, width);
        Assert.Equal(1, height);

        // Bgra8888 expects B, G, R, A per pixel, with A opaque.
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, buffer);
    }

    [Fact]
    public void CopyBgra_RejectsUndersizedDestination()
    {
        using var mat = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(0));

        Assert.Throws<ArgumentException>(
            () => BitmapBridge.TryCopyBgra(FrameWith(mat), new byte[8], out _, out _));
    }

    [Fact]
    public void CopyBgra_HandlesNonContinuousMat()
    {
        // A region of interest has a stride wider than its row; copying by the
        // source stride instead of the ROI width would shear the image.
        using var parent = new Mat(4, 8, MatType.CV_8UC3, Scalar.All(7));
        using var roi = new Mat(parent, new Rect(0, 0, 4, 4));

        Assert.False(roi.IsContinuous());

        var frame = FrameWith(roi);
        var buffer = new byte[BitmapBridge.RequiredBufferSize(frame)];

        Assert.True(BitmapBridge.TryCopyBgra(frame, buffer, out var width, out var height));
        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.All(buffer, b => Assert.True(b is 7 or 255));
    }

    [Fact]
    public void SaveToFile_WritesAnImage()
    {
        using var mat = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(128));
        var path = Path.Combine(Path.GetTempPath(), $"edgeforge-{Guid.NewGuid():N}.jpg");

        try
        {
            Assert.True(BitmapBridge.TrySaveToFile(FrameWith(mat), path));
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveToFile_SkipsFrameWithoutPixels()
    {
        var frame = new Frame("SYN-002", DateTimeOffset.UtcNow, 640, 480);
        var path = Path.Combine(Path.GetTempPath(), $"edgeforge-{Guid.NewGuid():N}.jpg");

        Assert.False(BitmapBridge.TrySaveToFile(frame, path));
        Assert.False(File.Exists(path));
    }
}
