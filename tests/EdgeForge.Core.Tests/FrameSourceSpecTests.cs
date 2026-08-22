using EdgeForge.Vision;
using OpenCvSharp;
using Xunit;

namespace EdgeForge.Core.Tests;

public class FrameSourceSpecTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    public void DigitsAreUsbDeviceIndex(string source, int expectedIndex)
    {
        var spec = FrameSourceSpec.Parse(source);

        Assert.Equal(FrameSourceKind.UsbDevice, spec.Kind);
        Assert.Equal(expectedIndex, spec.DeviceIndex);
        Assert.True(spec.IsLive);
    }

    [Fact]
    public void UsbUsesDirectShowOnWindows()
    {
        var spec = FrameSourceSpec.Parse("0");

        var expected = OperatingSystem.IsWindows()
            ? VideoCaptureAPIs.DSHOW
            : VideoCaptureAPIs.ANY;

        Assert.Equal(expected, spec.Api);
    }

    [Fact]
    public void RtspUrlKeepsCredentialsVerbatim()
    {
        const string url = "rtsp://admin:p%40ss@192.168.1.50:554/h264Preview_01_sub";

        var spec = FrameSourceSpec.Parse(url);

        Assert.Equal(FrameSourceKind.Rtsp, spec.Kind);
        Assert.Equal(VideoCaptureAPIs.FFMPEG, spec.Api);
        Assert.Equal(url, spec.Target);
        Assert.True(spec.IsLive);
    }

    [Fact]
    public void RtspSchemeIsCaseInsensitive()
    {
        var spec = FrameSourceSpec.Parse("RTSP://HOST/stream");

        Assert.Equal(FrameSourceKind.Rtsp, spec.Kind);
    }

    [Fact]
    public void PathIsTreatedAsFile()
    {
        var spec = FrameSourceSpec.Parse("samples/line.mp4");

        Assert.Equal(FrameSourceKind.File, spec.Kind);
        Assert.Equal(VideoCaptureAPIs.FFMPEG, spec.Api);
        // A file must never drop frames: replay has to stay deterministic.
        Assert.False(spec.IsLive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSourceIsRejected(string source)
    {
        Assert.Throws<ArgumentException>(() => FrameSourceSpec.Parse(source));
    }
}
