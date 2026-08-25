using EdgeForge.Core;
using OpenCvSharp;

namespace EdgeForge.Vision;

/// <summary>
/// Copies frame pixels into a plain BGRA byte buffer so a UI can build a bitmap
/// without referencing OpenCvSharp. The copy is deliberate: the frame's Mat is
/// disposed as soon as the pipeline is done with it, long before the UI thread
/// gets a chance to draw.
/// </summary>
public static class BitmapBridge
{
    private const int BytesPerPixel = 4;

    /// <summary>
    /// Bytes needed by <see cref="TryCopyBgra"/> for this frame, or 0 when the
    /// frame carries no readable pixels.
    /// </summary>
    public static int RequiredBufferSize(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var mat = ReadableMat(frame);
        return mat is null ? 0 : mat.Width * mat.Height * BytesPerPixel;
    }

    /// <summary>
    /// Copies the frame into <paramref name="destination"/> as tightly packed BGRA
    /// (stride = width * 4). Returns false when the frame has no readable pixels —
    /// a synthetic frame has <c>Image == null</c> and that is not an error, matching
    /// how <see cref="OnnxVisionDetector"/> treats the same case.
    /// </summary>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public static bool TryCopyBgra(Frame frame, Span<byte> destination, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(frame);

        width = 0;
        height = 0;

        var mat = ReadableMat(frame);
        if (mat is null) return false;

        var required = mat.Width * mat.Height * BytesPerPixel;
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} bytes, need {required}.",
                nameof(destination));
        }

        // Cv2.CvtColor allocates its own continuous buffer, which is what lets us
        // copy out in one span read regardless of the source Mat's stride.
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);

        unsafe
        {
            var source = new ReadOnlySpan<byte>(bgra.DataPointer, required);
            source.CopyTo(destination);
        }

        width = mat.Width;
        height = mat.Height;
        return true;
    }

    /// <summary>
    /// Writes the frame to disk; the extension picks the encoder (.png, .jpg).
    /// Returns false when the frame carries no pixels to save.
    /// </summary>
    public static bool TrySaveToFile(Frame frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var mat = ReadableMat(frame);
        if (mat is null) return false;

        return Cv2.ImWrite(path, mat);
    }

    /// <summary>
    /// Copies the frame's pixels into a new Mat the caller owns and must dispose,
    /// or null when the frame carries none.
    /// </summary>
    /// <remarks>
    /// For consumers that need pixels after the capture loop has moved on. The frame
    /// itself is freed as soon as the pipeline is done with it, so anything outliving
    /// that - a JPEG encoder on another thread, for instance - has to own a copy.
    /// A Mat clone is a native memcpy: cheap enough to do inside the capture loop,
    /// which is where the copy has to happen for the source to still be alive.
    /// </remarks>
    public static Mat? TryClonePixels(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return ReadableMat(frame)?.Clone();
    }

    /// <summary>
    /// Encodes the frame as a JPEG byte array. Returns false when the frame carries
    /// no readable pixels, matching <see cref="TryCopyBgra"/>.
    /// </summary>
    /// <param name="quality">1..100. Clamped rather than rejected.</param>
    public static bool TryEncodeJpeg(Frame frame, int quality, out byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var mat = ReadableMat(frame);
        if (mat is null)
        {
            jpeg = [];
            return false;
        }

        return TryEncodeJpeg(mat, quality, out jpeg);
    }

    /// <summary>
    /// Encodes a Mat as a JPEG byte array.
    /// </summary>
    /// <remarks>
    /// Takes a Mat rather than a Frame because the caller is the web layer's encoder
    /// task, which works on a clone it owns outright - the original frame is long
    /// disposed by then. Keeping the overload here rather than in the web project is
    /// what keeps OpenCvSharp confined to EdgeForge.Vision.
    /// </remarks>
    public static bool TryEncodeJpeg(Mat mat, int quality, out byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(mat);

        if (mat.Empty())
        {
            jpeg = [];
            return false;
        }

        var clamped = Math.Clamp(quality, 1, 100);
        var parameters = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, clamped) };

        // ImEncode returns void in this OpenCvSharp version and signals failure by
        // throwing, so treat an empty buffer as the failure case rather than a result.
        Cv2.ImEncode(".jpg", mat, out jpeg, parameters);
        return jpeg.Length > 0;
    }

    /// <summary>
    /// Draws detection boxes and labels onto the Mat, in place.
    /// </summary>
    /// <remarks>
    /// Mutates the Mat, so callers must pass a copy they own - never a frame the
    /// pipeline is still using.
    ///
    /// Boxes are baked into the pixels rather than drawn by the client because the
    /// stream has to survive players that cannot draw an overlay: VLC, ffplay, and
    /// the HLS path this leaves room for all show pixels and nothing else. It also
    /// sidesteps a synchronisation problem - MJPEG carries no frame number, so boxes
    /// fetched over a side channel would visibly lead or lag the image.
    /// </remarks>
    public static void DrawDetections(Mat mat, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(mat);
        ArgumentNullException.ThrowIfNull(detections);

        if (mat.Empty() || detections.Count == 0) return;

        // Scale the stroke with the image so a 2560-wide frame does not get hairlines
        // that vanish once a phone scales it down to 400 px.
        var thickness = Math.Max(2, mat.Width / 640);
        var fontScale = Math.Max(0.5, mat.Width / 1600.0);
        var colour = new Scalar(0x81, 0xe0, 0x7e);  // BGR of #7ee081, the overlay's green

        foreach (var detection in detections)
        {
            var box = detection.BoundingBox;

            // Denormalise, then clamp. A box can extend slightly outside 0..1 once the
            // letterbox is undone, and OpenCV throws on a rectangle outside the Mat.
            var x1 = (int)Math.Round(box.X * mat.Width);
            var y1 = (int)Math.Round(box.Y * mat.Height);
            var x2 = (int)Math.Round((box.X + box.Width) * mat.Width);
            var y2 = (int)Math.Round((box.Y + box.Height) * mat.Height);

            x1 = Math.Clamp(x1, 0, mat.Width - 1);
            y1 = Math.Clamp(y1, 0, mat.Height - 1);
            x2 = Math.Clamp(x2, 0, mat.Width - 1);
            y2 = Math.Clamp(y2, 0, mat.Height - 1);

            if (x2 <= x1 || y2 <= y1) continue;

            Cv2.Rectangle(mat, new Point(x1, y1), new Point(x2, y2), colour, thickness);

            var label = $"{detection.ClassName} {detection.Confidence:P0}";
            var size = Cv2.GetTextSize(
                label, HersheyFonts.HersheySimplex, fontScale, thickness, out var baseline);

            // Prefer the label above the box; drop it inside when the box hugs the top
            // edge, so it is never clipped off the frame.
            var labelBottom = y1 - baseline > size.Height ? y1 : y1 + size.Height + baseline;
            var labelTop = labelBottom - size.Height - baseline;
            var labelRight = Math.Min(x1 + size.Width, mat.Width - 1);

            Cv2.Rectangle(
                mat,
                new Point(x1, Math.Max(0, labelTop)),
                new Point(labelRight, Math.Min(mat.Height - 1, labelBottom)),
                colour,
                thickness: -1);

            Cv2.PutText(
                mat,
                label,
                new Point(x1, Math.Min(mat.Height - 1, labelBottom - baseline)),
                HersheyFonts.HersheySimplex,
                fontScale,
                Scalar.Black,
                thickness);
        }
    }

    private static Mat? ReadableMat(Frame frame)
    {
        // Mirrors OnnxVisionDetector.ReadablePixels: no Mat means no pixels, not a fault.
        if (frame.Image is not MatFrameImage matImage) return null;

        var mat = matImage.Mat;
        return mat.Empty() ? null : mat;
    }
}
