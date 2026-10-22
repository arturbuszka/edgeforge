using System.Globalization;
using EdgeForge.Core;

namespace EdgeForge.Decision;

/// <summary>A verdict and why.</summary>
public readonly record struct ItemJudgement(InspectionStatus Status, string? Reason);

/// <summary>
/// The acceptance rule for one item: reject classes and measurement limits, both
/// from configuration. What changes when the line carries something else.
/// </summary>
/// <remarks>
/// Two entry points, deliberately sharing every comparison. <see cref="Judge(Detection)"/>
/// reads one detection in one frame, for the viewer's colours and the per-frame
/// log. <see cref="Judge(IReadOnlyDictionary{string, double}, string?)"/> judges an
/// item once, from the medians of everything seen while it was tracked; that is
/// the verdict that becomes an event.
/// <para>
/// A missing measurement is UNKNOWN, never OK. An egg the camera never saw whole
/// has not been shown to be the right size, and passing it would make the
/// station's OK mean "not obviously wrong" instead of "checked".
/// </para>
/// </remarks>
public sealed class ItemRule
{
    private readonly LimitOptions[] _limits;
    private readonly HashSet<string> _rejectClasses;

    public ItemRule(IEnumerable<LimitOptions> limits, IEnumerable<string> rejectClasses)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(rejectClasses);

        _limits = limits.ToArray();
        _rejectClasses = new HashSet<string>(rejectClasses, StringComparer.Ordinal);
    }

    public static ItemRule From(LineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ItemRule(options.Limits, options.RejectClasses);
    }

    public IReadOnlyList<LimitOptions> Limits => _limits;

    public bool IsRejectClass(string className) => _rejectClasses.Contains(className);

    /// <summary>One detection, one frame: the reading the viewer colours a box by.</summary>
    public ItemJudgement Judge(Detection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);

        var defect = IsRejectClass(detection.ClassName) ? detection.ClassName : null;

        return Judge(detection.Measurements ?? Empty, defect);
    }

    /// <summary>
    /// Judges a set of measurements, with an optional defect class already
    /// established. NOK outranks UNKNOWN, which outranks OK.
    /// </summary>
    public ItemJudgement Judge(IReadOnlyDictionary<string, double> measurements, string? defect)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var rejections = new List<string>();
        string? missing = null;

        if (defect is not null) rejections.Add(defect);

        foreach (var limit in _limits)
        {
            if (!measurements.TryGetValue(limit.Measurement, out var value))
            {
                missing ??= $"no {limit.Measurement}";
                continue;
            }

            if (value < limit.Min || value > limit.Max)
            {
                rejections.Add(Describe(limit, value));
            }
        }

        if (rejections.Count > 0)
        {
            return new ItemJudgement(InspectionStatus.Nok, string.Join(", ", rejections));
        }

        return missing is not null
            ? new ItemJudgement(InspectionStatus.Unknown, missing)
            : new ItemJudgement(InspectionStatus.Ok, null);
    }

    private static string Describe(LimitOptions limit, double value)
    {
        if (!string.IsNullOrWhiteSpace(limit.Reason)) return limit.Reason;

        var bound = value < limit.Min
            ? $"< {limit.Min.Value.ToString("0.##", CultureInfo.InvariantCulture)}"
            : $"> {limit.Max!.Value.ToString("0.##", CultureInfo.InvariantCulture)}";

        return $"{limit.Measurement} {value.ToString("0.0", CultureInfo.InvariantCulture)} {bound}";
    }

    private static readonly IReadOnlyDictionary<string, double> Empty =
        new Dictionary<string, double>();
}
