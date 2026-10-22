using System.Globalization;
using EdgeForge.Core;

namespace EdgeForge.Decision;

/// <summary>Counts since the run started, as one consistent reading.</summary>
/// <param name="PerMinute">
/// Throughput over the last minute of scene time, or null until two items have
/// been judged - one item is not a rate.
/// </param>
public sealed record LineSnapshot(
    int Judged,
    int Ok,
    int Nok,
    int Unknown,
    IReadOnlyDictionary<string, int> NokReasons,
    ItemVerdict? Last,
    double? PerMinute,
    int InView);

/// <summary>
/// Where the line scenario meets the pipeline: feeds each frame's detections to
/// <see cref="LineCounter"/>, counts the verdicts, and appends them to the journal.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="MealEventSink"/>, decorating the inner action for
/// the same reasons: the per-frame reading stays visible, and journal I/O lands in
/// ActionLatency rather than in DecisionLatency.
/// <para>
/// Counts are per run, not per day. A line runs in shifts and batches, and "since
/// I pressed Start" is the number an operator watching a demo or a trial batch
/// wants. The journal keeps everything for the per-day question.
/// </para>
/// <para>
/// Uses <see cref="InspectionResult.Timestamp"/>, the frame's scene time, so a
/// recorded run reports the throughput the belt actually had rather than however
/// fast the file decoded.
/// </para>
/// </remarks>
public sealed class LineEventSink : IDecisionSink
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    private readonly IInspectionAction _inner;
    private readonly LineCounter _counter;
    private readonly ItemJournal? _journal;
    private readonly Action<string>? _notify;
    private readonly Lock _gate = new();

    private readonly Dictionary<string, int> _nokReasons = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recent = new();
    private int _ok;
    private int _nok;
    private int _unknown;
    private int _inView;
    private ItemVerdict? _last;

    public LineEventSink(
        IInspectionAction inner,
        LineCounter counter,
        ItemJournal? journal = null,
        Action<string>? notify = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(counter);

        _inner = inner;
        _counter = counter;
        _journal = journal;
        _notify = notify;
    }

    /// <summary>Raised on the capture thread for every verdict, after it is counted.</summary>
    public event Action<ItemVerdict>? Verdict;

    public LineSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new LineSnapshot(
                    _ok + _nok + _unknown,
                    _ok,
                    _nok,
                    _unknown,
                    new Dictionary<string, int>(_nokReasons, StringComparer.Ordinal),
                    _last,
                    RatePerMinute(),
                    _inView);
            }
        }
    }

    public async Task ExecuteAsync(InspectionResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await _inner.ExecuteAsync(result, cancellationToken);

        var verdicts = _counter.Observe(result.Timestamp, result.Detections);

        lock (_gate) _inView = _counter.ActiveItems;

        foreach (var verdict in verdicts) Record(verdict);
    }

    /// <summary>
    /// Nothing to close: an item not yet at the line has no verdict to give, and
    /// inventing one at shutdown would be judging on partial evidence.
    /// </summary>
    public void Flush()
    {
        lock (_gate) _inView = 0;
    }

    public IReadOnlyList<ReadoutLine> Readout()
    {
        var snapshot = Snapshot;
        var lines = new List<ReadoutLine>
        {
            new("Items judged", Invariant(snapshot.Judged)),
            new("OK / NOK / ?", $"{snapshot.Ok} / {snapshot.Nok} / {snapshot.Unknown}"),
            new("Reject rate", snapshot.Judged == 0
                ? "-"
                : ((double)snapshot.Nok / snapshot.Judged).ToString("P1", CultureInfo.InvariantCulture)),
            new("Items / min", snapshot.PerMinute is { } rate
                ? rate.ToString("F0", CultureInfo.InvariantCulture)
                : "-"),
            new("Last item", snapshot.Last is { } last ? Describe(last) : "-")
        };

        foreach (var (reason, count) in snapshot.NokReasons.OrderByDescending(r => r.Value))
        {
            lines.Add(new ReadoutLine($"NOK {reason}", Invariant(count)));
        }

        return lines;
    }

    /// <summary>One verdict as a log line: number, status, reason, measurements.</summary>
    public static string Describe(ItemVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        var status = verdict.Status switch
        {
            InspectionStatus.Ok => "OK",
            InspectionStatus.Nok => "NOK",
            _ => "UNKNOWN"
        };

        var reason = verdict.Reason is null ? "" : $" {verdict.Reason}";
        var measured = verdict.Measurements.Count == 0
            ? ""
            : " — " + string.Join(", ", verdict.Measurements
                .OrderBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => $"{m.Key} {m.Value.ToString("F1", CultureInfo.InvariantCulture)}"));

        return $"#{verdict.ItemId} {status}{reason}{measured}";
    }

    private void Record(ItemVerdict verdict)
    {
        lock (_gate)
        {
            switch (verdict.Status)
            {
                case InspectionStatus.Ok:
                    _ok++;
                    break;
                case InspectionStatus.Nok:
                    _nok++;
                    var reason = verdict.Reason ?? "NOK";
                    _nokReasons[reason] = _nokReasons.GetValueOrDefault(reason) + 1;
                    break;
                default:
                    _unknown++;
                    break;
            }

            _last = verdict;

            _recent.Enqueue(verdict.At);
            while (_recent.Count > 0 && verdict.At - _recent.Peek() > RateWindow) _recent.Dequeue();
        }

        _notify?.Invoke(Describe(verdict));
        Verdict?.Invoke(verdict);

        if (_journal is null) return;

        try
        {
            _journal.Append(verdict);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same posture as the meal journal: a full disk loses history, not the line.
            _notify?.Invoke($"Could not write item history: {ex.Message}");
        }
    }

    /// <summary>Items per minute across the window, from first to last verdict in it. Call under _gate.</summary>
    private double? RatePerMinute()
    {
        if (_recent.Count < 2) return null;

        var span = _recent.Last() - _recent.Peek();
        return span > TimeSpan.Zero ? (_recent.Count - 1) / span.TotalMinutes : null;
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
