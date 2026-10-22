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
    /// Which detector to build: "yolo" (an ONNX model, or the placeholder when
    /// <see cref="ModelPath"/> is empty) or "blob" (classical segmentation and
    /// measurement, configured under <c>Blob</c>).
    /// </summary>
    public string Kind { get; set; } = DetectorKinds.Yolo;

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
    /// Names for every class of a custom-trained model, in training order, e.g.
    /// ["egg", "egg_cracked", "egg_dirty"]. When set, every class is kept and
    /// <see cref="ClassId"/> and <see cref="ClassName"/> are ignored. Empty keeps
    /// the single-class COCO behaviour.
    /// </summary>
    public string[] ClassNames { get; set; } = [];

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

/// <summary>
/// Turning a stream of detections into meal events, and where those events land.
/// </summary>
/// <remarks>
/// The decision layer. Without it "seven meals" is 1400 frames carrying a
/// detection rather than seven events.
///
/// The three thresholds are chosen from how a cat actually eats - a visit lasts
/// tens of seconds and contains pauses - and they are configuration rather than
/// constants because the honest way to set them is to run for a few days, read
/// back <c>meals.jsonl</c>, and adjust. Zero or negative values are rejected at
/// startup rather than producing an event stream that quietly makes no sense.
/// </remarks>
public sealed class MealsOptions
{
    /// <summary>Turn meal counting on. Off leaves the pipeline exactly as it was.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds the animal must be present before a meal starts. Rejects one
    /// walking past the bowls; at 23 fps five seconds is ~115 frames of evidence.
    /// </summary>
    public double MinPresenceSeconds { get; set; } = 5;

    /// <summary>
    /// Seconds of absence that end a meal. The number most worth tuning: cats
    /// leave the bowl and come back, and a gap under a minute is one meal with a
    /// pause rather than two meals.
    /// </summary>
    public double MaxAbsenceSeconds { get; set; } = 60;

    /// <summary>
    /// Shortest meal worth logging, first sighting to last. Filters the animal
    /// that lingered just past the start threshold without eating.
    /// </summary>
    public double MinMealSeconds { get; set; } = 10;

    /// <summary>
    /// Where the overlay snapshot goes. Empty means "stats.json" beside the
    /// binary, which is where an OBS Browser Source reads it from. Set to null-ish
    /// only if nothing is reading it.
    /// </summary>
    public string StatsPath { get; set; } = "";

    /// <summary>
    /// Append-only meal history. Empty means "meals.jsonl" beside the binary.
    /// Also what today's counter is rebuilt from after a restart.
    /// </summary>
    public string JournalPath { get; set; } = "";

    /// <summary>
    /// Floor on how often <see cref="StatsPath"/> is rewritten. A completed meal
    /// and a change of state always write at once; this only throttles the idle
    /// heartbeat, so the disk is not touched at camera framerate.
    /// </summary>
    public double StatsIntervalSeconds { get; set; } = 5;

    /// <exception cref="InvalidOperationException">A threshold is not usable.</exception>
    public void Validate()
    {
        if (MinPresenceSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"Meals:MinPresenceSeconds must be greater than 0, got {MinPresenceSeconds}.");
        }

        if (MaxAbsenceSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"Meals:MaxAbsenceSeconds must be greater than 0, got {MaxAbsenceSeconds}.");
        }

        if (MinMealSeconds < 0)
        {
            throw new InvalidOperationException(
                $"Meals:MinMealSeconds must not be negative, got {MinMealSeconds}.");
        }

        if (StatsIntervalSeconds < 0)
        {
            throw new InvalidOperationException(
                $"Meals:StatsIntervalSeconds must not be negative, got {StatsIntervalSeconds}.");
        }
    }
}

/// <summary>
/// The local API and its live HUD: a REST surface plus a SignalR push channel,
/// served by the separate EdgeForge.Api executable.
/// </summary>
/// <remarks>
/// Video does NOT come from here. MediaMTX pulls the camera's RTSP stream and
/// republishes it to the browser over WebRTC; this process serves counts and
/// timestamps only. That is why "only events leave the device, never frames"
/// holds again without qualification - the predecessor to this section,
/// <c>WebOptions</c>, configured an MJPEG stream and was a deliberate exception
/// to it.
///
/// It is still a household surface on a private network, not a product one: no
/// accounts, no roles, no authentication framework. Nothing in the staged plan
/// may depend on it.
/// </remarks>
public sealed class ApiOptions
{
    /// <summary>
    /// Address to bind. "127.0.0.1" is loopback only. "0.0.0.0" binds every
    /// interface, including the tailnet one and the LAN. A specific tailnet address
    /// (100.x.y.z, from "tailscale ip -4") binds only the tailnet, which is the
    /// tightest setting that still works from another device.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5080;

    /// <summary>
    /// WHEP endpoint the browser POSTs its SDP offer to, served by MediaMTX. Handed
    /// to the front-end through /api/config so the built bundle carries no
    /// host-specific URL and the same bundle works from a phone or a tailnet.
    /// </summary>
    public string StreamUrl { get; set; } = "http://127.0.0.1:8889/cat/whep";

    /// <summary>HLS fallback, for Safari and anything WebRTC cannot reach.</summary>
    public string HlsUrl { get; set; } = "http://127.0.0.1:8888/cat/index.m3u8";

    /// <summary>
    /// Origins allowed in development, when Vite serves the front-end on its own
    /// port. In production the bundle is served from this origin and CORS does not
    /// apply, so this is empty-able without losing anything.
    /// </summary>
    public string[] DevOrigins { get; set; } = ["http://localhost:5173"];

    /// <summary>
    /// Floor on how often the live snapshot is recomputed and pushed on change.
    /// One second is plenty for a HUD and keeps SignalR off camera framerate.
    /// </summary>
    public double PushIntervalSeconds { get; set; } = 1;

    /// <exception cref="InvalidOperationException">A value is not usable.</exception>
    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Api:Port must be between 1 and 65535, got {Port}.");
        }

        if (string.IsNullOrWhiteSpace(BindAddress))
        {
            throw new InvalidOperationException("Api:BindAddress must not be empty.");
        }

        if (!System.Net.IPAddress.TryParse(BindAddress, out _))
        {
            throw new InvalidOperationException(
                $"Api:BindAddress must be an IP address, got '{BindAddress}'. " +
                "Use 0.0.0.0 for every interface, or the tailnet address from 'tailscale ip -4'.");
        }

        // A relative URL here would resolve against the API's own origin, and the
        // stream never comes from there - it comes from MediaMTX on another port.
        // Catching it at startup beats a front-end that loads and shows no video.
        if (!IsHttpUrl(StreamUrl))
        {
            throw new InvalidOperationException(
                $"Api:StreamUrl must be an absolute URL, got '{StreamUrl}'. " +
                "It points at MediaMTX, not at this process.");
        }

        if (!IsHttpUrl(HlsUrl))
        {
            throw new InvalidOperationException(
                $"Api:HlsUrl must be an absolute URL, got '{HlsUrl}'. " +
                "It points at MediaMTX, not at this process.");
        }

        if (PushIntervalSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"Api:PushIntervalSeconds must be greater than 0, got {PushIntervalSeconds}.");
        }
    }

    /// <summary>
    /// An absolute http(s) URL. The scheme check is not decoration: on Linux and
    /// macOS <c>Uri.TryCreate("/cat/whep", UriKind.Absolute, ...)</c> succeeds and
    /// yields <c>file:///cat/whep</c>, so "absolute" alone lets a relative path
    /// through on every platform except Windows.
    /// </summary>
    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// The queryable mirror of the meal journal.
/// </summary>
/// <remarks>
/// <c>meals.jsonl</c> stays the durable append-only record; this database is a view
/// over the same events, existing because a flat file cannot answer "meals between
/// these two dates" without reading all of it. It is rebuildable by definition:
/// delete the file, restart, and the journal refills it.
/// </remarks>
public sealed class StorageOptions
{
    /// <summary>Where the SQLite file lives. Empty means "meals.db" beside the binary.</summary>
    public string DatabasePath { get; set; } = "";

    /// <exception cref="InvalidOperationException">The path is not usable.</exception>
    public void Validate()
    {
        // Empty is the documented default, not a mistake; only a value that cannot
        // be a path at all is worth refusing.
        if (DatabasePath.Length > 0 && DatabasePath.Trim().Length == 0)
        {
            throw new InvalidOperationException(
                "Storage:DatabasePath must not be whitespace. Leave it empty for the default.");
        }
    }
}

public sealed class EdgeForgeOptions
{
    /// <summary>
    /// What the installation is watching: "bowls" (meal events) or "line" (one
    /// verdict per item crossing a decision line). See <see cref="ScenarioNames"/>.
    /// </summary>
    public string Scenario { get; set; } = ScenarioNames.Bowls;

    public CameraOptions Camera { get; set; } = new();

    public DetectorOptions Detector { get; set; } = new();

    public CaptureOptions Capture { get; set; } = new();

    public MealsOptions Meals { get; set; } = new();

    public ApiOptions Api { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    public BlobOptions Blob { get; set; } = new();

    public LineOptions Line { get; set; } = new();

    /// <summary>
    /// Checks the scenario name, the detector kind, and the sections those two
    /// actually use. A section that is not in use is not validated: a half-edited
    /// Line block must not stop the bowls from running.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not usable.</exception>
    public void ValidateScenario()
    {
        ScenarioNames.Validate(Scenario);
        DetectorKinds.Validate(Detector.Kind);

        if (Detector.Kind == DetectorKinds.Blob) Blob.Validate();
        if (Scenario == ScenarioNames.Line) Line.Validate();

        // Meals are validated where the sink is built, as before: the viewer
        // treats a bad meal threshold as "counting off", not as a refusal to run.
    }
}
