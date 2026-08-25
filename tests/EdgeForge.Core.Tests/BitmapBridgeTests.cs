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

    [Fact]
    public void EncodeJpeg_ProducesADecodableImage()
    {
        using var mat = new Mat(16, 24, MatType.CV_8UC3, Scalar.All(128));

        Assert.True(BitmapBridge.TryEncodeJpeg(FrameWith(mat), 75, out var jpeg));

        // JPEG SOI marker: anything else means we handed the browser a body it
        // cannot render, which shows up as a broken image rather than an exception.
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Equal(0xFF, jpeg[2]);

        using var decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.Equal(24, decoded.Width);
        Assert.Equal(16, decoded.Height);
    }

    [Fact]
    public void EncodeJpeg_SkipsFrameWithoutPixels()
    {
        var frame = new Frame("SYN-003", DateTimeOffset.UtcNow, 640, 480);

        Assert.False(BitmapBridge.TryEncodeJpeg(frame, 75, out var jpeg));
        Assert.Empty(jpeg);
    }

    [Fact]
    public void EncodeJpeg_ClampsQualityRatherThanThrowing()
    {
        // The quality comes from configuration. A typo should cost image quality,
        // not take the stream down mid-view.
        using var mat = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(64));

        Assert.True(BitmapBridge.TryEncodeJpeg(mat, -5, out var low));
        Assert.True(BitmapBridge.TryEncodeJpeg(mat, 500, out var high));
        Assert.NotEmpty(low);
        Assert.NotEmpty(high);
    }

    [Fact]
    public void DrawDetections_MarksThePixelsItCovers()
    {
        using var mat = new Mat(100, 100, MatType.CV_8UC3, Scalar.All(0));
        var detections = new[]
        {
            new Detection("cat", 0.88, new BoundingBox(0.25, 0.25, 0.5, 0.5))
        };

        BitmapBridge.DrawDetections(mat, detections);

        // Something was drawn: an all-black Mat has no non-zero pixels.
        Assert.True(Cv2.CountNonZero(mat.CvtColor(ColorConversionCodes.BGR2GRAY)) > 0);
    }

    [Fact]
    public void DrawDetections_SurvivesABoxOutsideTheFrame()
    {
        // Undoing the letterbox can push a box just past the edge. OpenCV throws on
        // a rectangle outside the Mat, which would kill the encoder task and with it
        // every viewer - so the clamp is load-bearing, not defensive tidiness.
        using var mat = new Mat(50, 50, MatType.CV_8UC3, Scalar.All(0));
        var detections = new[]
        {
            new Detection("cat", 0.5, new BoundingBox(-0.2, -0.2, 1.6, 1.6)),
            new Detection("cat", 0.5, new BoundingBox(0.98, 0.98, 0.5, 0.5))
        };

        BitmapBridge.DrawDetections(mat, detections);
    }

    [Fact]
    public void DrawDetections_WithNoDetectionsLeavesTheFrameAlone()
    {
        using var mat = new Mat(20, 20, MatType.CV_8UC3, Scalar.All(0));

        BitmapBridge.DrawDetections(mat, []);

        Assert.Equal(0, Cv2.CountNonZero(mat.CvtColor(ColorConversionCodes.BGR2GRAY)));
    }
}
