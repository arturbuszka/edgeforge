using EdgeForge.Vision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// A machine without DirectML must still run the detector. This needs the native
/// runtime to load, so it opens a real session, on a one-node model small enough to
/// live in this file.
/// </summary>
public class OnnxProviderFallbackTests
{
    private const string IdentityModel =
        "CAg6NwoQCgF4EgF5IghJZGVudGl0eRIBZ1oPCgF4EgoKCAgBEgQKAggBYg8KAXkSCgoICAESBAoCCAFCBAoAEA0=";

    [Fact]
    public void DirectMlRequest_OpensASession_AndReportsTheProviderItGot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"edgeforge-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, Convert.FromBase64String(IdentityModel));

        try
        {
            using var detector = new OnnxVisionDetector(path, executionProvider: "directml");

            if (OperatingSystem.IsWindows())
            {
                // A runner may or may not expose a DX12 adapter; either answer is valid.
                Assert.True(
                    detector.ActiveProvider == "cpu"
                    || detector.ActiveProvider.StartsWith("directml:", StringComparison.Ordinal),
                    detector.ActiveProvider);
            }
            else
            {
                Assert.Equal("cpu", detector.ActiveProvider);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
