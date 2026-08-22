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

        var detector = new OnnxVisionDetector(
            options.ModelPath,
            options.NetSize,
            options.ScoreThreshold,
            options.NmsThreshold,
            options.ClassId,
            options.ClassName);

        var inspector = new CountInspector(
            options.ExpectedCount,
            options.ClassName);

        return (detector, inspector);
    }

    /// <summary>One line describing the active model, for a status bar or console banner.</summary>
    public static string Describe(DetectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.IsNullOrWhiteSpace(options.ModelPath)
            ? "none (SampleDetector placeholder)"
            : $"{options.ModelPath} ({options.NetSize}px, class {options.ClassId} " +
              $"'{options.ClassName}', expecting {options.ExpectedCount})";
    }
}
