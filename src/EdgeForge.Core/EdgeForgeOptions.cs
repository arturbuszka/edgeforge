namespace EdgeForge.Core;

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

public sealed class DetectorOptions
{
    /// <summary>
    /// Path to an exported YOLOv8 .onnx model. Empty runs the deterministic
    /// placeholder detector instead, so the demo works with no model on disk.
    /// </summary>
    public string ModelPath { get; set; } = "";

    /// <summary>Square model input size; must match the exported imgsz.</summary>
    public int NetSize { get; set; } = 640;

    public float ScoreThreshold { get; set; } = 0.25f;

    public float NmsThreshold { get; set; } = 0.45f;

    /// <summary>
    /// COCO class index to keep. 15 = cat (development target, indoors and always
    /// available), 14 = bird for the coop. Moving from one to the other is this
    /// number and <see cref="ClassName"/>, nothing else.
    /// </summary>
    public int ClassId { get; set; } = 15;

    /// <summary>Domain name reported for detections of <see cref="ClassId"/>.</summary>
    public string ClassName { get; set; } = "cat";

    /// <summary>
    /// Population a count rule expects to see in frame. Currently unread: the
    /// bowls run <c>PresenceInspector</c>, where zero animals is the normal state
    /// rather than a fault. It stays for <c>CountInspector</c>, which is the right
    /// rule for a scene with a known, fixed population such as the coop.
    /// </summary>
    public int ExpectedCount { get; set; } = 2;

    /// <summary>
    /// Where inference runs: "directml" for the GPU, "cpu" to force the CPU
    /// provider. DirectML is the default because on this hardware it is roughly
    /// four times faster and, more usefully, flat across model sizes - yolov8m
    /// costs about what yolov8n does, so accuracy stops trading against latency.
    /// Falls back to the CPU on its own if no DirectML device can be opened, so a
    /// machine without a DX12 GPU still runs.
    /// </summary>
    public string ExecutionProvider { get; set; } = "directml";

    /// <summary>
    /// Which GPU DirectML should use, as a DXGI adapter index. Ignored when
    /// <see cref="ExecutionProvider"/> is "cpu".
    /// </summary>
    /// <remarks>
    /// This matters more than it looks on a laptop. Index 0 is not the fastest
    /// adapter, it is whichever one DXGI enumerates first, and on a machine with
    /// switchable graphics that is usually the integrated GPU. Measured here on a
    /// Ryzen 3750H with a GTX 1660 Ti, yolov8m: adapter 0 (Radeon Vega 10) 193 ms,
    /// adapter 1 (GTX 1660 Ti) 28 ms - a 7x difference from one integer.
    /// The startup banner prints the index in use; if inference is far slower than
    /// expected, try the other one before assuming anything else is wrong.
    /// <para>
    /// There is no reliable way to derive the right index automatically: DXGI
    /// order does not match the order the adapters appear in the driver registry,
    /// so a heuristic based on video memory picks confidently and wrongly. It stays
    /// an explicit setting, and the banner warns when more than one GPU is present.
    /// </para>
    /// </remarks>
    public int GpuDeviceId { get; set; }
}

/// <summary>
/// Writing frames to disk. Used by the live viewer only; the headless demo
/// ignores this section. Every frame saved here is training data for telling
/// individual animals apart later, so the defaults favour a slow, steady
/// trickle over a flood that fills the disk in an afternoon.
/// </summary>
public sealed class CaptureOptions
{
    /// <summary>Where to write frames. Empty means "captures" beside the binary.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Automatically save frames that contain detections.</summary>
    public bool AutoSaveDetections { get; set; }

    /// <summary>Floor on the gap between two auto-saves. Guards the disk.</summary>
    public double MinIntervalSeconds { get; set; } = 5.0;

    /// <summary>Only auto-save when the best detection is at least this confident.</summary>
    public double MinConfidence { get; set; } = 0.5;

    /// <summary>Hard ceiling on files written per session; 0 means unlimited.</summary>
    public int MaxFiles { get; set; } = 5000;
}

public sealed class EdgeForgeOptions
{
    public CameraOptions Camera { get; set; } = new();

    public DetectorOptions Detector { get; set; } = new();

    public CaptureOptions Capture { get; set; } = new();
}
