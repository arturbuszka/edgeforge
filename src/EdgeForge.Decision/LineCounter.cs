using EdgeForge.Core;

namespace EdgeForge.Decision;

/// <summary>
/// The one verdict an item gets, at the moment it crosses the decision line.
/// </summary>
/// <param name="ItemId">Item number within this run, as the tracker assigned it.</param>
/// <param name="At">Scene time of the frame in which the item crossed the line.</param>
/// <param name="Measurements">
/// Median of each measurement over every frame the item was seen whole. Medians,
/// not means: one frame with a bad contour - glare, a neighbour touching - moves a
/// mean and leaves a median where it was.
/// </param>
/// <param name="ObservedFrames">Frames the item was seen in before the verdict.</param>
public sealed record ItemVerdict(
    int ItemId,
    DateTimeOffset At,
    InspectionStatus Status,
    string? Reason,
    IReadOnlyDictionary<string, double> Measurements,
    int ObservedFrames);

/// <summary>
/// Turns per-frame detections into one verdict per item: tracks items across
/// frames and judges each one as it crosses the decision line.
/// </summary>
/// <remarks>
/// Pure, like <see cref="MealDetector"/>: timestamps in, verdicts out, no clock and
/// no I/O. A recorded run replays to the same verdicts.
/// <para>
/// An item is judged only when it crosses the line in the direction of travel. One
/// that is first seen already past the line never is - it entered downstream of
/// the decision point, and a verdict on it would be a verdict on half the evidence.
/// One that disappears before the line is not judged either; it never reached the
/// point where the station decides.
/// </para>
/// </remarks>
public sealed class LineCounter
{
    private readonly ItemTracker _tracker;
    private readonly ItemRule _rule;
    private readonly double _line;
    private readonly LineDirection _direction;
    private readonly int _minRejectFrames;

    public LineCounter(LineOptions options, ItemRule? rule = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _tracker = new ItemTracker(options.MaxJump, TimeSpan.FromSeconds(options.MaxAbsenceSeconds));
        _rule = rule ?? ItemRule.From(options);
        _line = options.DecisionLine;
        _direction = options.Direction;
        _minRejectFrames = options.MinRejectFrames;
    }

    /// <summary>Items currently in view and being followed.</summary>
    public int ActiveItems => _tracker.Active.Count;

    /// <summary>Feeds one frame in; returns the verdicts for items that crossed the line in it.</summary>
    public IReadOnlyList<ItemVerdict> Observe(DateTimeOffset timestamp, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);

        var observed = _tracker.Update(timestamp, detections);

        List<ItemVerdict>? verdicts = null;

        foreach (var item in observed)
        {
            if (item.Judged || item.PreviousCentre is not { } previous) continue;

            if (Progress(previous) < _line && Progress(item.Centre) >= _line)
            {
                item.Judged = true;
                (verdicts ??= []).Add(Verdict(item, timestamp));
            }
        }

        return verdicts ?? (IReadOnlyList<ItemVerdict>)[];
    }

    /// <summary>Position along the direction of travel, increasing downstream.</summary>
    private double Progress(NormalisedPoint point) => _direction switch
    {
        LineDirection.LeftToRight => point.X,
        LineDirection.RightToLeft => 1 - point.X,
        LineDirection.TopToBottom => point.Y,
        _ => 1 - point.Y
    };

    private ItemVerdict Verdict(TrackedItem item, DateTimeOffset at)
    {
        var medians = Medians(item.Observations);
        var judgement = _rule.Judge(medians, Defect(item.Observations));

        return new ItemVerdict(
            item.Id,
            at,
            judgement.Status,
            judgement.Reason,
            medians,
            item.Observations.Count);
    }

    /// <summary>
    /// The reject class seen most often on this item, if it was seen in at least
    /// <see cref="LineOptions.MinRejectFrames"/> frames. A single frame of "cracked"
    /// is the detector's opinion on one image, not a finding about the egg.
    /// </summary>
    private string? Defect(IReadOnlyList<Detection> observations)
    {
        string? worst = null;
        var worstCount = 0;

        foreach (var group in observations
                     .Where(o => _rule.IsRejectClass(o.ClassName))
                     .GroupBy(o => o.ClassName, StringComparer.Ordinal))
        {
            var count = group.Count();
            if (count >= _minRejectFrames && count > worstCount)
            {
                worst = group.Key;
                worstCount = count;
            }
        }

        return worst;
    }

    private static Dictionary<string, double> Medians(IReadOnlyList<Detection> observations)
    {
        var values = new Dictionary<string, List<double>>(StringComparer.Ordinal);

        foreach (var observation in observations)
        {
            if (observation.Measurements is null) continue;

            foreach (var (name, value) in observation.Measurements)
            {
                if (!values.TryGetValue(name, out var list)) values[name] = list = [];
                list.Add(value);
            }
        }

        var medians = new Dictionary<string, double>(values.Count, StringComparer.Ordinal);
        foreach (var (name, list) in values)
        {
            list.Sort();
            var middle = list.Count / 2;
            medians[name] = list.Count % 2 == 1
                ? list[middle]
                : (list[middle - 1] + list[middle]) / 2.0;
        }

        return medians;
    }
}
