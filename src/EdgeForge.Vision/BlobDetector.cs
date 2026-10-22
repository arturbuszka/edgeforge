using EdgeForge.Core;
using OpenCvSharp;

namespace EdgeForge.Vision;

/// <summary>
/// Finds objects that contrast with a plain background and measures each one by
/// the ellipse fitted to its outline. No model: threshold, contour, fit.
/// </summary>
/// <remarks>
/// Built for an egg on a belt, and general to anything roughly elliptical on a
/// plain background - a tablet, a nut, a bottle cap seen from above. It reports
/// two measurements per object, the long and short axis of the fitted ellipse,
/// in millimetres when <see cref="BlobOptions.MmPerPixel"/> is set and in source
/// pixels otherwise.
/// <para>
/// An object touching the frame edge is still reported - the tracker needs to see
/// it arrive - but WITHOUT measurements. Half an egg measures as a small egg, and
/// the decision layer would reject it as undersize on evidence that was never
/// about its size.
/// </para>
/// <para>
/// Confidence here is not a probability. It is the fill ratio: contour area over
/// the area of the fitted ellipse, clamped to 1. An egg scores close to 1; two
/// touching eggs merged into one blob, or a smear of glare, score visibly lower,
/// which is exactly the case worth seeing in amber on the viewer.
/// </para>
/// </remarks>
public sealed class BlobDetector : IVisionDetector
{
    private readonly BlobOptions _options;

    public BlobDetector(BlobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public Task<IReadOnlyList<Detection>> DetectAsync(
        Frame frame,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mat = OnnxVisionDetector.ReadablePixels(frame);

        // A few milliseconds of OpenCV on the capture thread: unlike ONNX
        // inference, not worth a hop to the thread pool.
        IReadOnlyList<Detection> detections = mat is null ? [] : Detect(mat);

        return Task.FromResult(detections);
    }

    /// <summary>Detects and measures objects in a BGR or greyscale image.</summary>
    public IReadOnlyList<Detection> Detect(Mat image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Empty()) return [];

        var width = image.Width;
        var height = image.Height;

        using var binary = Segment(image);

        Cv2.FindContours(
            binary,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxNone);

        var frameArea = (double)width * height;
        var minArea = _options.MinAreaFraction * frameArea;
        var maxArea = _options.MaxAreaFraction * frameArea;

        var detections = new List<Detection>();

        foreach (var contour in contours)
        {
            var area = Cv2.ContourArea(contour);
            if (area < minArea || area > maxArea) continue;

            // FitEllipse needs five points; anything passing the area floor has
            // far more, but the guard costs nothing.
            if (contour.Length < 5) continue;

            var rect = Cv2.BoundingRect(contour);
            var ellipse = Cv2.FitEllipse(contour);

            var major = Math.Max(ellipse.Size.Width, ellipse.Size.Height);
            var minor = Math.Min(ellipse.Size.Width, ellipse.Size.Height);
            var ellipseArea = Math.PI / 4.0 * major * minor;
            var fill = ellipseArea > 0 ? Math.Min(1.0, area / ellipseArea) : 0.0;

            var box = new BoundingBox(
                (double)rect.X / width,
                (double)rect.Y / height,
                (double)rect.Width / width,
                (double)rect.Height / height);

            detections.Add(new Detection(
                _options.ClassName,
                fill,
                box,
                TouchesEdge(rect, width, height) ? null : Measure(major, minor)));
        }

        return detections;
    }

    /// <summary>Greyscale, blur, threshold, then a small open and close.</summary>
    /// <remarks>
    /// The open removes specks of glare and dust smaller than the kernel; the close
    /// fills pinholes inside an object - a speckled shell would otherwise give a
    /// ragged contour and a fitted ellipse a millimetre off.
    /// </remarks>
    private Mat Segment(Mat image)
    {
        using var gray = new Mat();
        if (image.Channels() == 1) image.CopyTo(gray);
        else Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);

        if (_options.BlurKernel > 0)
        {
            Cv2.GaussianBlur(gray, gray, new Size(_options.BlurKernel, _options.BlurKernel), 0);
        }

        var type = _options.BrightObjects ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv;

        var binary = new Mat();
        if (_options.Threshold == 0)
        {
            Cv2.Threshold(gray, binary, 0, 255, type | ThresholdTypes.Otsu);
        }
        else
        {
            Cv2.Threshold(gray, binary, _options.Threshold, 255, type);
        }

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);
        Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);

        return binary;
    }

    private Dictionary<string, double> Measure(double majorPx, double minorPx)
    {
        var scale = _options.MmPerPixel;

        return scale > 0
            ? new Dictionary<string, double>
            {
                [MeasurementNames.LengthMm] = majorPx * scale,
                [MeasurementNames.WidthMm] = minorPx * scale
            }
            : new Dictionary<string, double>
            {
                [MeasurementNames.LengthPx] = majorPx,
                [MeasurementNames.WidthPx] = minorPx
            };
    }

    /// <summary>Within one pixel of any border: the object may continue outside the frame.</summary>
    private static bool TouchesEdge(Rect rect, int width, int height) =>
        rect.X <= 1 || rect.Y <= 1 || rect.Right >= width - 1 || rect.Bottom >= height - 1;
}
