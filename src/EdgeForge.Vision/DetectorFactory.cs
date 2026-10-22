using EdgeForge.Core;

namespace EdgeForge.Vision;

/// <summary>
/// Builds the detector/inspector pair from configuration. Lives here rather than
/// in Runtime because every type it constructs is a Vision adapter, and Runtime
/// is deliberately free of OpenCvSharp and OnnxRuntime.
/// </summary>
public static class DetectorFactory
{
    /// <summary>
    /// An empty ModelPath keeps the placeholder detector, so both the demo and the
    /// viewer still run end to end on a machine with no model exported.
    /// </summary>
    /// <exception cref="FileNotFoundException">The model file is missing.</exception>
    /// <exception cref="Microsoft.ML.OnnxRuntime.OnnxRuntimeException">
    /// The model exists but could not be loaded.
    /// </exception>
    public static (IVisionDetector Detector, IInspector Inspector) Create(DetectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            return (new SampleDetector(), new LabelInspector());
        }

        var detector = CreateOnnx(options);

        // Presence, not count: at the bowls zero animals is the normal state, and
        // CountInspector would mark almost every frame of the day Nok. See
        // PresenceInspector; CountInspector remains for a known-population scene.
        var inspector = new PresenceInspector(options.ClassName);

        return (detector, inspector);
    }

    /// <summary>
    /// Builds the detector alone, for a scenario that pairs it with its own rule.
    /// </summary>
    /// <remarks>
    /// "yolo" with no model path still gives the placeholder, so a fresh clone runs
    /// end to end. "blob" needs no model at all.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The Kind or a Blob value is not usable.</exception>
    public static IVisionDetector CreateDetector(DetectorOptions options, BlobOptions blob)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(blob);

        DetectorKinds.Validate(options.Kind);

        if (options.Kind == DetectorKinds.Blob) return new BlobDetector(blob);

        return string.IsNullOrWhiteSpace(options.ModelPath)
            ? new SampleDetector()
            : CreateOnnx(options);
    }

    private static OnnxVisionDetector CreateOnnx(DetectorOptions options) =>
        new(
            options.ModelPath,
            options.NetSize,
            options.ScoreThreshold,
            options.NmsThreshold,
            options.ClassId,
            options.ClassName,
            options.ExecutionProvider,
            options.GpuDeviceId,
            options.ClassNames);

    /// <summary>One line describing the active model, for a status bar or console banner.</summary>
    /// <param name="detector">
    /// The detector <see cref="Create"/> returned, if there is one. Passing it makes
    /// the banner report the provider the session actually opened with rather than
    /// the one that was asked for - the two differ whenever DirectML fell back to
    /// the CPU, which is exactly when you want to know.
    /// </param>
    public static string Describe(DetectorOptions options, IVisionDetector? detector = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (detector is BlobDetector)
        {
            return "blob segmentation (no model)";
        }

        if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            return "none (SampleDetector placeholder)";
        }

        var provider = detector is OnnxVisionDetector onnx
            ? onnx.ActiveProvider
            : options.ExecutionProvider;

        var classes = options.ClassNames.Length > 0
            ? $"classes [{string.Join(", ", options.ClassNames)}]"
            : $"class {options.ClassId} '{options.ClassName}'";

        var line = $"{options.ModelPath} ({options.NetSize}px, {classes}, {provider})";

        // Selecting the wrong GPU costs about 7x here and shows up as nothing but
        // slowness, so say out loud what the alternatives were.
        if (provider.StartsWith("directml", StringComparison.Ordinal)
            && GpuAdapters.HasMultipleAdapters(out var adapters))
        {
            line += $"{Environment.NewLine}         GPUs present: {adapters}. " +
                    $"Using adapter {options.GpuDeviceId}; if inference is slow, " +
                    "try another Detector.GpuDeviceId.";
        }

        return line;
    }
}
