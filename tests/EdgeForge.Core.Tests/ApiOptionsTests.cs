using EdgeForge.Core;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// Configuration guards for the local API and the SQLite mirror.
/// </summary>
/// <remarks>
/// The predecessor of this file, <c>WebOptionsTests</c>, opened by asserting that
/// the web layer was off by default - the whole carve-out from "only events leave
/// the device" rested on it. There is no such assertion here, and its absence is
/// the point: this process serves counts and timestamps, never frames, so it needs
/// no carve-out to be on. What replaces it is <see cref="StreamUrlMustBeAbsolute"/>,
/// which guards the seam where video actually lives now.
/// </remarks>
public class ApiOptionsTests
{
    [Fact]
    public void BindsToLoopbackByDefault()
    {
        // The default must not reach the LAN without someone deciding it should.
        Assert.Equal("127.0.0.1", new ApiOptions().BindAddress);
    }

    [Fact]
    public void DefaultsAreValid()
    {
        new ApiOptions().Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void RejectsAPortOutOfRange(int port)
    {
        var options = new ApiOptions { Port = port };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Api:Port", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsAnEmptyBindAddress(string address)
    {
        var options = new ApiOptions { BindAddress = address };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Api:BindAddress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAHostnameAsBindAddress()
    {
        // Kestrel is handed an IPAddress, so a name that resolves at the shell is
        // still a startup crash here. Saying so in configuration is cheaper.
        var options = new ApiOptions { BindAddress = "localhost" };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Api:BindAddress", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/cat/whep")]
    [InlineData("cat/whep")]
    [InlineData("")]
    public void StreamUrlMustBeAbsolute(string url)
    {
        // A relative URL resolves against this API's own origin, and the stream
        // never comes from there - it comes from MediaMTX on another port. The
        // symptom without this check is a page that loads and shows no video.
        var options = new ApiOptions { StreamUrl = url };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Api:StreamUrl", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HlsUrlMustBeAbsolute()
    {
        var options = new ApiOptions { HlsUrl = "/cat/index.m3u8" };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Api:HlsUrl", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositivePushInterval(double seconds)
    {
        var options = new ApiOptions { PushIntervalSeconds = seconds };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Api:PushIntervalSeconds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultStreamUrlPointsAtMediaMtxNotAtThisProcess()
    {
        // Guards against someone "tidying" the default to a relative path once the
        // front-end is served from this origin. The bundle is same-origin; the
        // stream is not.
        var options = new ApiOptions();

        Assert.NotEqual(options.Port, new Uri(options.StreamUrl).Port);
    }

    [Fact]
    public void StorageDefaultsAreValid()
    {
        new StorageOptions().Validate();
    }

    [Fact]
    public void StorageRejectsAWhitespaceDatabasePath()
    {
        // Empty is the documented default. Whitespace is a typo that would
        // otherwise become a file named " " beside the binary.
        var options = new StorageOptions { DatabasePath = "  " };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Storage:DatabasePath", error.Message, StringComparison.Ordinal);
    }
}
