using EdgeForge.Vision;
using OpenCvSharp;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The measuring instrument. Frames are drawn with known geometry, so every
/// assertion is against a size we chose rather than one we read back.
/// </summary>
public class BlobDetectorTests
{
    private const double MmPerPixel = 0.5;

    private static BlobOptions Options(bool calibrated = true) => new()
    {
        ClassName = "egg",
        MmPerPixel = calibrated ? MmPerPixel : 0,
        MinAreaFraction = 0.002,
        MaxAreaFraction = 0.3
    };

    /// <summary>A dark belt with a light ellipse of the given full axes, in millimetres.</summary>
    private static Mat Belt(params (double CentreX, double CentreY, double LengthMm, double WidthMm, double Angle)[] eggs)
    {
        var mat = new Mat(new Size(640, 360), MatType.CV_8UC3, new Scalar(40, 40, 40));

        foreach (var egg in eggs)
        {
            Cv2.Ellipse(
                mat,
                new RotatedRect(
                    new Point2f((float)egg.CentreX, (float)egg.CentreY),
                    new Size2f((float)(egg.LengthMm / MmPerPixel), (float)(egg.WidthMm / MmPerPixel)),
                    (float)egg.Angle),
                new Scalar(190, 215, 230),
                thickness: -1);
        }

        return mat;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(90)]
    public void MeasuresAnEggWithinHalfAMillimetre_AtAnyRotation(double angle)
    {
        using var belt = Belt((320, 180, 57, 44, angle));

        var detection = Assert.Single(new BlobDetector(Options()).Detect(belt));

        Assert.NotNull(detection.Measurements);
        Assert.Equal(57, detection.Measurements[MeasurementNames.LengthMm], tolerance: 0.5);
        Assert.Equal(44, detection.Measurements[MeasurementNames.WidthMm], tolerance: 0.5);
        Assert.Equal("egg", detection.ClassName);
    }

    [Fact]
    public void TellsASmallEggFromANormalOne()
    {
        using var belt = Belt((180, 180, 57, 44, 0), (460, 180, 50, 38, 0));

        var widths = new BlobDetector(Options()).Detect(belt)
            .OrderBy(d => d.BoundingBox.X)
            .Select(d => d.Measurements![MeasurementNames.WidthMm])
            .ToArray();

        Assert.Equal(2, widths.Length);
        Assert.Equal(44, widths[0], tolerance: 0.5);
        Assert.Equal(38, widths[1], tolerance: 0.5);
    }

    [Fact]
    public void AnEggCutByTheFrameEdge_IsReportedButNotMeasured()
    {
        // Half an egg measures as a small egg. The tracker still needs to see it
        // arrive, so it is reported - but with no size to be judged on.
        using var belt = Belt((10, 180, 57, 44, 0));

        var detection = Assert.Single(new BlobDetector(Options()).Detect(belt));

        Assert.Null(detection.Measurements);
    }

    [Fact]
    public void DustBelowTheAreaFloor_IsNotAnObject()
    {
        using var belt = Belt((320, 180, 57, 44, 0));
        Cv2.Circle(belt, new Point(80, 80), 3, new Scalar(230, 230, 230), thickness: -1);

        Assert.Single(new BlobDetector(Options()).Detect(belt));
    }

    [Fact]
    public void AnEmptyNoisyBelt_HasNoObjects()
    {
        // Otsu on a frame with nothing in it still splits the noise in two. The
        // blur, the morphological open and the area floor must between them leave
        // nothing - an empty belt reporting eggs would count phantoms all shift.
        using var belt = new Mat(new Size(640, 360), MatType.CV_8UC3, new Scalar(40, 40, 40));
        using var noise = new Mat(belt.Size(), MatType.CV_8UC3);
        Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(8, 8, 8));
        Cv2.Add(belt, noise, belt);

        Assert.Empty(new BlobDetector(Options()).Detect(belt));
    }

    [Fact]
    public void DarkObjectsOnALightBelt_AreFoundWhenConfiguredSo()
    {
        using var belt = new Mat(new Size(640, 360), MatType.CV_8UC3, new Scalar(220, 220, 220));
        Cv2.Ellipse(
            belt,
            new RotatedRect(new Point2f(320, 180), new Size2f(114, 88), 0),
            new Scalar(60, 50, 40),
            thickness: -1);

        var options = Options();
        options.BrightObjects = false;

        var detection = Assert.Single(new BlobDetector(options).Detect(belt));
        Assert.Equal(44, detection.Measurements![MeasurementNames.WidthMm], tolerance: 0.5);
    }

    [Fact]
    public void WithoutCalibration_MeasuresInPixels()
    {
        using var belt = Belt((320, 180, 57, 44, 0));

        var detection = Assert.Single(new BlobDetector(Options(calibrated: false)).Detect(belt));

        Assert.False(detection.Measurements!.ContainsKey(MeasurementNames.WidthMm));
        Assert.Equal(88, detection.Measurements[MeasurementNames.WidthPx], tolerance: 1.0);
    }

    [Fact]
    public void ConfidenceIsTheFillRatio_HighForAnEgg()
    {
        using var belt = Belt((320, 180, 57, 44, 0));

        var detection = Assert.Single(new BlobDetector(Options()).Detect(belt));

        Assert.InRange(detection.Confidence, 0.95, 1.0);
    }

    [Fact]
    public void BoxIsNormalised()
    {
        using var belt = Belt((320, 180, 57, 44, 0));

        var box = Assert.Single(new BlobDetector(Options()).Detect(belt)).BoundingBox;

        // 114 px long on a 640 px frame, centred.
        Assert.Equal(114.0 / 640, box.Width, tolerance: 0.01);
        Assert.Equal(0.5, box.X + box.Width / 2, tolerance: 0.01);
    }

    [Fact]
    public async Task AFrameWithoutPixels_IsAnEmptyObservation()
    {
        var detections = await new BlobDetector(Options())
            .DetectAsync(new Frame("SYN-1", DateTimeOffset.UtcNow, 640, 360));

        Assert.Empty(detections);
    }
}
