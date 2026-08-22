using EdgeForge.Core;
using OpenCvSharp;

namespace EdgeForge.Vision;

/// <summary>
/// Zero-copy adapter: holds the <see cref="Mat"/> rather than copying its pixels,
/// so the core domain stays free of OpenCvSharp while detectors keep full-speed
/// access via pattern matching on this type.
/// </summary>
public sealed class MatFrameImage : IFrameImage
{
    private bool _disposed;

    public MatFrameImage(Mat mat) => Mat = mat;

    public Mat Mat { get; }

    public int Width => Mat.Width;

    public int Height => Mat.Height;

    // Idempotent: the drop path and the shutdown drain can race toward the same
    // instance, and a double free on a native Mat is not a recoverable mistake.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Mat.Dispose();
    }
}
