namespace EdgeForge.Core;

/// <summary>
/// Names of the scenarios a host can run. A scenario is the part of the system
/// that changes with what is in front of the camera: which detector, which
/// per-frame rule, and which decision layer turns readings into events.
/// Everything else - capture, pipeline, metrics, viewer - is shared.
/// </summary>
public static class ScenarioNames
{
    /// <summary>Animals at the bowls: presence debounced into meal events.</summary>
    public const string Bowls = "bowls";

    /// <summary>Items passing a decision line on a conveyor: one verdict per item.</summary>
    public const string Line = "line";

    /// <exception cref="InvalidOperationException">The name is not a known scenario.</exception>
    public static void Validate(string scenario)
    {
        if (scenario is Bowls or Line) return;

        throw new InvalidOperationException(
            $"Scenario must be '{Bowls}' or '{Line}', got '{scenario}'.");
    }
}

/// <summary>Names for <see cref="DetectorOptions.Kind"/>.</summary>
public static class DetectorKinds
{
    /// <summary>A YOLOv8 ONNX model, or the placeholder when no model is set.</summary>
    public const string Yolo = "yolo";

    /// <summary>Classical segmentation: contrasting objects on a plain background.</summary>
    public const string Blob = "blob";

    /// <exception cref="InvalidOperationException">The name is not a known kind.</exception>
    public static void Validate(string kind)
    {
        if (kind is Yolo or Blob) return;

        throw new InvalidOperationException(
            $"Detector:Kind must be '{Yolo}' or '{Blob}', got '{kind}'.");
    }
}

/// <summary>
/// The segmentation detector: objects that contrast with a plain background,
/// found by threshold and contour and measured by a fitted ellipse.
/// </summary>
/// <remarks>
/// No model, no training data, and deterministic: the same frame always gives the
/// same measurement. It needs what any measuring station needs anyway - a plain
/// background, even light, and a camera fixed square to the belt. Under those
/// conditions geometry is a better instrument than a neural network, and the
/// network is kept for what geometry cannot see, such as a crack.
/// </remarks>
public sealed class BlobOptions
{
    /// <summary>Class name reported for every object found.</summary>
    public string ClassName { get; set; } = "egg";

    /// <summary>
    /// True when objects are lighter than the background (eggs on a dark belt),
    /// false for dark objects on a light one.
    /// </summary>
    public bool BrightObjects { get; set; } = true;

    /// <summary>
    /// Grey level 1..255 separating object from background, or 0 to let Otsu's
    /// method pick it per frame. Otsu adapts to drifting light; a fixed value is
    /// steadier once the lighting is controlled.
    /// </summary>
    public int Threshold { get; set; }

    /// <summary>Smallest object, as a fraction of frame area. Rejects dust and glare.</summary>
    public double MinAreaFraction { get; set; } = 0.002;

    /// <summary>Largest object, as a fraction of frame area. Rejects a lighting change.</summary>
    public double MaxAreaFraction { get; set; } = 0.25;

    /// <summary>
    /// Calibration: millimetres per source pixel, measured once with a ruler in
    /// frame. 0 leaves the camera uncalibrated and measurements in pixels.
    /// </summary>
    public double MmPerPixel { get; set; }

    /// <summary>Gaussian blur kernel before thresholding; odd, or 0 for none.</summary>
    public int BlurKernel { get; set; } = 5;

    /// <exception cref="InvalidOperationException">A value is not usable.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClassName))
        {
            throw new InvalidOperationException("Blob:ClassName must not be empty.");
        }

        if (Threshold is < 0 or > 255)
        {
            throw new InvalidOperationException(
                $"Blob:Threshold must be 0 (Otsu) or 1..255, got {Threshold}.");
        }

        if (MinAreaFraction <= 0 || MaxAreaFraction <= MinAreaFraction || MaxAreaFraction > 1)
        {
            throw new InvalidOperationException(
                "Blob:MinAreaFraction and Blob:MaxAreaFraction must satisfy " +
                $"0 < min < max <= 1, got {MinAreaFraction} and {MaxAreaFraction}.");
        }

        if (MmPerPixel < 0)
        {
            throw new InvalidOperationException(
                $"Blob:MmPerPixel must not be negative, got {MmPerPixel}.");
        }

        if (BlurKernel < 0 || (BlurKernel > 0 && BlurKernel % 2 == 0))
        {
            throw new InvalidOperationException(
                $"Blob:BlurKernel must be 0 or an odd number, got {BlurKernel}.");
        }
    }
}

/// <summary>Which way items travel across the frame.</summary>
public enum LineDirection
{
    LeftToRight,
    RightToLeft,
    TopToBottom,
    BottomToTop
}

/// <summary>
/// One acceptance limit on a measurement: the item is rejected with
/// <see cref="Reason"/> when the measured value falls outside [Min, Max].
/// </summary>
public sealed class LimitOptions
{
    /// <summary>Measurement key, e.g. "width_mm". See <see cref="MeasurementNames"/>.</summary>
    public string Measurement { get; set; } = "";

    /// <summary>Lowest acceptable value, or null for no lower limit.</summary>
    public double? Min { get; set; }

    /// <summary>Highest acceptable value, or null for no upper limit.</summary>
    public double? Max { get; set; }

    /// <summary>What the rejection is called in the event log, e.g. "Undersize".</summary>
    public string Reason { get; set; } = "";
}

/// <summary>
/// The conveyor scenario: items are tracked across frames and judged once, at the
/// moment they cross the decision line.
/// </summary>
/// <remarks>
/// Judging per frame would give one item thirty verdicts, and the event log would
/// count frames rather than eggs. Judging at a line gives each item exactly one,
/// at a known place - which is also where a reject gate would sit, so the event
/// and the actuator agree about which item they are talking about.
/// </remarks>
public sealed class LineOptions
{
    /// <summary>Position of the decision line along the direction of travel, 0..1.</summary>
    public double DecisionLine { get; set; } = 0.5;

    public LineDirection Direction { get; set; } = LineDirection.LeftToRight;

    /// <summary>
    /// Furthest an item's centre may stray from where its last movement predicted
    /// and still be the same item, in normalised frame units (1 = the full width
    /// or height). Too large and two neighbouring items swap identities.
    /// </summary>
    public double MaxJump { get; set; } = 0.15;

    /// <summary>
    /// Seconds an item may go unseen before its track is dropped. Measured in time,
    /// not frames, for the same reason meals are: a stalled pipeline must not read
    /// as an item vanishing.
    /// </summary>
    public double MaxAbsenceSeconds { get; set; } = 0.5;

    /// <summary>
    /// Frames in which a reject class must be seen on an item before it counts.
    /// One frame of "cracked" is a detector's opinion; two is evidence.
    /// </summary>
    public int MinRejectFrames { get; set; } = 2;

    /// <summary>Detection classes that reject an item outright, e.g. "egg_cracked".</summary>
    public string[] RejectClasses { get; set; } = [];

    /// <summary>Acceptance limits on measurements, checked against the per-item median.</summary>
    public LimitOptions[] Limits { get; set; } = [];

    /// <summary>Append-only verdict history. Empty means "items.jsonl" beside the binary.</summary>
    public string JournalPath { get; set; } = "";

    /// <exception cref="InvalidOperationException">A value is not usable.</exception>
    public void Validate()
    {
        if (DecisionLine is <= 0 or >= 1)
        {
            throw new InvalidOperationException(
                $"Line:DecisionLine must be strictly between 0 and 1, got {DecisionLine}.");
        }

        if (MaxJump is <= 0 or > 1)
        {
            throw new InvalidOperationException(
                $"Line:MaxJump must be in (0, 1], got {MaxJump}.");
        }

        if (MaxAbsenceSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"Line:MaxAbsenceSeconds must be greater than 0, got {MaxAbsenceSeconds}.");
        }

        if (MinRejectFrames < 1)
        {
            throw new InvalidOperationException(
                $"Line:MinRejectFrames must be at least 1, got {MinRejectFrames}.");
        }

        foreach (var limit in Limits)
        {
            if (string.IsNullOrWhiteSpace(limit.Measurement))
            {
                throw new InvalidOperationException("Line:Limits: every limit needs a Measurement.");
            }

            if (limit.Min is null && limit.Max is null)
            {
                throw new InvalidOperationException(
                    $"Line:Limits: '{limit.Measurement}' has neither Min nor Max.");
            }

            if (limit.Min > limit.Max)
            {
                throw new InvalidOperationException(
                    $"Line:Limits: '{limit.Measurement}' has Min {limit.Min} above Max {limit.Max}.");
            }
        }
    }
}
