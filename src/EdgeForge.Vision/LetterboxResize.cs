using OpenCvSharp;

namespace EdgeForge.Vision;

/// <summary>
/// Aspect-preserving resize with padding (letterbox). YOLOX and YOLOv8 share the
/// same preprocessing here: the source image is scaled by a single
/// <see cref="Ratio"/> so it fits inside the network input, then padded
/// (bottom/right) with 114.
/// </summary>
public sealed class LetterboxResult : IDisposable
{
    public required Mat Padded { get; init; }

    /// <summary>Scale applied to the original image (out = in * Ratio).</summary>
    public required double Ratio { get; init; }

    public void Dispose() => Padded.Dispose();
}

public static class LetterboxResize
{
    /// <summary>Pad value used by YOLOX and YOLOv8 (grey, 114 on each channel).</summary>
    private const double PadValue = 114.0;

    /// <summary>
    /// Resizes <paramref name="src"/> (BGR) into a <paramref name="netWidth"/> x
    /// <paramref name="netHeight"/> canvas, preserving aspect ratio. Divide model
    /// output coordinates by <see cref="LetterboxResult.Ratio"/> to map back to the
    /// original image.
    /// </summary>
    public static LetterboxResult Apply(Mat src, int netWidth, int netHeight)
    {
        double ratio = Math.Min(
            netWidth / (double)src.Width,
            netHeight / (double)src.Height);

        int resizedW = (int)Math.Round(src.Width * ratio);
        int resizedH = (int)Math.Round(src.Height * ratio);

        var canvas = new Mat(netHeight, netWidth, src.Type(), Scalar.All(PadValue));
        using var resized = new Mat();
        Cv2.Resize(src, resized, new Size(resizedW, resizedH), interpolation: InterpolationFlags.Linear);
        using (Mat roi = new(canvas, new Rect(0, 0, resizedW, resizedH)))
        {
            resized.CopyTo(roi);
        }

        return new LetterboxResult { Padded = canvas, Ratio = ratio };
    }
}
