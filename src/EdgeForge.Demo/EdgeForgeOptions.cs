namespace EdgeForge.Demo;

public sealed class CameraOptions
{
    /// <summary>
    /// USB device index ("0"), an rtsp:// URL, or a path to a video file.
    /// For a camera that needs credentials, keep the full URL out of source
    /// control and supply it via EDGEFORGE_Camera__Source instead.
    /// </summary>
    public string Source { get; set; } = "samples/line.mp4";

    /// <summary>Bounded channel depth between capture and inspection.</summary>
    public int Capacity { get; set; } = 1;
}

public sealed class EdgeForgeOptions
{
    public CameraOptions Camera { get; set; } = new();
}
