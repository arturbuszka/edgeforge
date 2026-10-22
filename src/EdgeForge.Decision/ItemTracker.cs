using EdgeForge.Core;

namespace EdgeForge.Decision;

/// <summary>A point in normalised frame coordinates, 0..1 on each axis.</summary>
public readonly record struct NormalisedPoint(double X, double Y)
{
    public static NormalisedPoint CentreOf(BoundingBox box) =>
        new(box.X + box.Width / 2.0, box.Y + box.Height / 2.0);

    public double DistanceTo(NormalisedPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>One object followed across frames, and everything seen of it.</summary>
public sealed class TrackedItem
{
    private readonly List<Detection> _observations = [];

    internal TrackedItem(int id, DateTimeOffset seen, Detection detection)
    {
        Id = id;
        FirstSeen = seen;
        LastSeen = seen;
        Centre = NormalisedPoint.CentreOf(detection.BoundingBox);
        _observations.Add(detection);
    }

    /// <summary>Sequential within one tracker; an item number, not an identity across restarts.</summary>
    public int Id { get; }

    public DateTimeOffset FirstSeen { get; }

    public DateTimeOffset LastSeen { get; private set; }

    public NormalisedPoint Centre { get; private set; }

    /// <summary>Centre at the previous observation, or null on the first one.</summary>
    public NormalisedPoint? PreviousCentre { get; private set; }

    /// <summary>Every detection matched to this item, oldest first.</summary>
    public IReadOnlyList<Detection> Observations => _observations;

    /// <summary>Set by the consumer once it has acted on this item; the tracker ignores it.</summary>
    public bool Judged { get; set; }

    /// <summary>
    /// Where the item should be now if it kept moving as it did between the last
    /// two observations. A belt moves at constant speed, so this is a far better
    /// guess than "where it was" and lets a fast belt use a small match radius.
    /// </summary>
    internal NormalisedPoint Predicted =>
        PreviousCentre is { } previous
            ? new NormalisedPoint(2 * Centre.X - previous.X, 2 * Centre.Y - previous.Y)
            : Centre;

    internal void Observe(DateTimeOffset seen, Detection detection)
    {
        PreviousCentre = Centre;
        Centre = NormalisedPoint.CentreOf(detection.BoundingBox);
        LastSeen = seen;
        _observations.Add(detection);
    }
}

/// <summary>
/// Follows objects from frame to frame by position: each detection is matched to
/// the nearest predicted position within a radius, or starts a new item.
/// </summary>
/// <remarks>
/// Nearest-neighbour with constant-velocity prediction, matched greedily from the
/// closest pair outward. That is enough for objects on a belt, which move in one
/// direction at one speed and never overtake each other. It is not enough for
/// animals that cross paths, and is not presented as such: per-animal identity
/// needs appearance, not just position.
/// <para>
/// Pure: no clock, no I/O. Absence is measured in the timestamps it is given, so
/// a stalled pipeline does not end a track and a replayed file tracks the same
/// way twice.
/// </para>
/// </remarks>
public sealed class ItemTracker
{
    private readonly double _maxJump;
    private readonly TimeSpan _maxAbsence;
    private readonly List<TrackedItem> _active = [];
    private int _nextId = 1;

    /// <param name="maxJump">Largest match distance, in normalised frame units.</param>
    /// <param name="maxAbsence">How long an item may go unseen before it is dropped.</param>
    public ItemTracker(double maxJump, TimeSpan maxAbsence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxJump);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAbsence, TimeSpan.Zero);

        _maxJump = maxJump;
        _maxAbsence = maxAbsence;
    }

    /// <summary>Items currently being followed.</summary>
    public IReadOnlyList<TrackedItem> Active => _active;

    /// <summary>
    /// Matches one frame's detections to tracked items.
    /// </summary>
    /// <returns>The items observed in this frame, new ones included.</returns>
    public IReadOnlyList<TrackedItem> Update(DateTimeOffset timestamp, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);

        // Expire first, so a detection cannot be matched to an item that by this
        // frame's clock has already been gone too long.
        _active.RemoveAll(item => timestamp - item.LastSeen > _maxAbsence);

        var pairs = new List<(int Item, int Detection, double Distance)>();
        for (var i = 0; i < _active.Count; i++)
        {
            var predicted = _active[i].Predicted;
            for (var d = 0; d < detections.Count; d++)
            {
                var distance = predicted.DistanceTo(NormalisedPoint.CentreOf(detections[d].BoundingBox));
                if (distance <= _maxJump) pairs.Add((i, d, distance));
            }
        }

        pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var itemTaken = new bool[_active.Count];
        var detectionTaken = new bool[detections.Count];
        var observed = new List<TrackedItem>(detections.Count);

        foreach (var (item, detection, _) in pairs)
        {
            if (itemTaken[item] || detectionTaken[detection]) continue;

            itemTaken[item] = true;
            detectionTaken[detection] = true;

            _active[item].Observe(timestamp, detections[detection]);
            observed.Add(_active[item]);
        }

        for (var d = 0; d < detections.Count; d++)
        {
            if (detectionTaken[d]) continue;

            var created = new TrackedItem(_nextId++, timestamp, detections[d]);
            _active.Add(created);
            observed.Add(created);
        }

        return observed;
    }
}
