using System.Globalization;
using OpenCvSharp;

namespace EdgeForge.Vision;

public enum FrameSourceKind
{
    UsbDevice,
    Rtsp,
    File
}

/// <summary>
/// Parsed description of where frames come from. Pure and side-effect free, so
/// the source-selection rules are testable without any camera attached.
/// </summary>
public sealed record FrameSourceSpec(
    FrameSourceKind Kind,
    string Target,
    int DeviceIndex,
    VideoCaptureAPIs Api)
{
    /// <summary>True for self-clocking sources that outrun a slow consumer.</summary>
    public bool IsLive => Kind is FrameSourceKind.UsbDevice or FrameSourceKind.Rtsp;

    public static FrameSourceSpec Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException(
                "Frame source must be a USB device index (e.g. \"0\"), " +
                "an rtsp:// URL, or a path to a video file.",
                nameof(source));
        }

        var trimmed = source.Trim();

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            // DSHOW over MSMF: MSMF has a long-standing multi-second open latency
            // and property-set failures with many UVC webcams.
            var api = OperatingSystem.IsWindows()
                ? VideoCaptureAPIs.DSHOW
                : VideoCaptureAPIs.ANY;

            return new FrameSourceSpec(FrameSourceKind.UsbDevice, trimmed, index, api);
        }

        if (trimmed.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
        {
            return new FrameSourceSpec(FrameSourceKind.Rtsp, trimmed, -1, VideoCaptureAPIs.FFMPEG);
        }

        return new FrameSourceSpec(FrameSourceKind.File, trimmed, -1, VideoCaptureAPIs.FFMPEG);
    }
}
