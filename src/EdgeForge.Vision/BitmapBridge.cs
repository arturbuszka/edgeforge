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

    private static Mat? ReadableMat(Frame frame)
    {
        // Mirrors OnnxVisionDetector.ReadablePixels: no Mat means no pixels, not a fault.
        if (frame.Image is not MatFrameImage matImage) return null;

        var mat = matImage.Mat;
        return mat.Empty() ? null : mat;
    }
}
