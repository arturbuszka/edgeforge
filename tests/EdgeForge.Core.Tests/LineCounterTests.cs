using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// One verdict per item. The case that motivates the whole class: an egg seen in
/// thirty frames is ONE egg, judged once, at the line.
/// </summary>
public class LineCounterTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FrameStep = TimeSpan.FromMilliseconds(40);

    private static LineOptions Options(Action<LineOptions>? configure = null)
    {
        var options = new LineOptions
        {
            DecisionLine = 0.5,
            Direction = LineDirection.LeftToRight,
            MaxJump = 0.1,
            MaxAbsenceSeconds = 0.5,
            MinRejectFrames = 2,
            RejectClasses = ["egg_cracked"],
            Limits = [new LimitOptions { Measurement = "width_mm", Min = 42, Reason = "Undersize" }]
        };

        configure?.Invoke(options);
        return options;
    }

    private static Detection Egg(double centreX, double? widthMm = 44, string className = "egg", double centreY = 0.5)
    {
        const double size = 0.1;
        var measurements = widthMm is { } width
            ? new Dictionary<string, double> { ["width_mm"] = width, ["length_mm"] = width * 1.3 }
            : null;

        return new Detection(
            className, 0.98,
            new BoundingBox(centreX - size / 2, centreY - size / 2, size, size),
            measurements);
    }

    /// <summary>
    /// Moves one egg from <paramref name="from"/> to <paramref name="to"/> in steps,
    /// returning every verdict along the way.
    /// </summary>
    private static List<ItemVerdict> Run(
        LineCounter counter,
        ref DateTimeOffset now,
        double from,
        double to,
        double step,
        Func<int, double, Detection?> egg)
    {
        var verdicts = new List<ItemVerdict>();
        var frame = 0;

        for (var x = from; step > 0 ? x <= to : x >= to; x += step, frame++)
        {
            var detection = egg(frame, x);
            verdicts.AddRange(counter.Observe(now, detection is null ? [] : [detection]));
            now += FrameStep;
        }

        return verdicts;
    }

    [Fact]
    public void AnEggCrossingTheFrame_GetsExactlyOneVerdict()
    {
        var counter = new LineCounter(Options());
        var now = Origin;

        var verdicts = Run(counter, ref now, 0.05, 0.95, 0.03, (_, x) => Egg(x));

        var verdict = Assert.Single(verdicts);
        Assert.Equal(InspectionStatus.Ok, verdict.Status);
        Assert.Equal(44, verdict.Measurements["width_mm"], tolerance: 0.001);
        Assert.True(verdict.ObservedFrames > 10, $"judged on {verdict.ObservedFrames} frames");
    }

    [Fact]
    public void AnUndersizeEgg_IsNok_WithTheConfiguredReason()
    {
        var counter = new LineCounter(Options());
        var now = Origin;

        var verdict = Assert.Single(Run(counter, ref now, 0.05, 0.95, 0.03, (_, x) => Egg(x, widthMm: 39)));

        Assert.Equal(InspectionStatus.Nok, verdict.Status);
        Assert.Equal("Undersize", verdict.Reason);
    }

    [Fact]
    public void AnEggNeverSeenWhole_IsUnknown_NeverOk()
    {
        // The station did not measure it. Passing it would make OK mean "nothing
        // looked wrong" rather than "checked".
        var counter = new LineCounter(Options());
        var now = Origin;

        var verdict = Assert.Single(Run(counter, ref now, 0.05, 0.95, 0.03, (_, x) => Egg(x, widthMm: null)));

        Assert.Equal(InspectionStatus.Unknown, verdict.Status);
        Assert.Contains("width_mm", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OneBadFrame_DoesNotMoveTheMedian()
    {
        // A neighbour touching, a flare of glare: one frame measures 30 mm. The
        // verdict is on the median, and the egg stays OK.
        var counter = new LineCounter(Options());
        var now = Origin;

        var verdict = Assert.Single(Run(counter, ref now, 0.05, 0.95, 0.03,
            (frame, x) => Egg(x, widthMm: frame == 5 ? 30 : 44)));

        Assert.Equal(InspectionStatus.Ok, verdict.Status);
    }

    [Fact]
    public void ADefectSeenInOneFrame_IsNotAFinding()
    {
        var counter = new LineCounter(Options());
        var now = Origin;

        var verdict = Assert.Single(Run(counter, ref now, 0.05, 0.95, 0.03,
            (frame, x) => Egg(x, className: frame == 3 ? "egg_cracked" : "egg")));

        Assert.Equal(InspectionStatus.Ok, verdict.Status);
    }

    [Fact]
    public void ADefectSeenInTwoFrames_RejectsTheEgg_EvenAtFullSize()
    {
        var counter = new LineCounter(Options());
        var now = Origin;

        var verdict = Assert.Single(Run(counter, ref now, 0.05, 0.95, 0.03,
            (frame, x) => Egg(x, className: frame is 3 or 7 ? "egg_cracked" : "egg")));

        Assert.Equal(InspectionStatus.Nok, verdict.Status);
        Assert.Equal("egg_cracked", verdict.Reason);
    }

    [Fact]
    public void AnEggFirstSeenPastTheLine_IsNotJudged()
    {
        // It entered downstream of the decision point; a verdict on it would rest
        // on half the evidence.
        var counter = new LineCounter(Options());
        var now = Origin;

        Assert.Empty(Run(counter, ref now, 0.6, 0.95, 0.03, (_, x) => Egg(x)));
    }

    [Fact]
    public void AnEggThatStopsBeforeTheLine_IsNotJudged()
    {
        var counter = new LineCounter(Options());
        var now = Origin;

        Assert.Empty(Run(counter, ref now, 0.05, 0.45, 0.03, (_, x) => Egg(x)));
    }

    [Fact]
    public void TwoEggsInView_AreTwoItems_EachJudgedOnItsOwnSize()
    {
        var counter = new LineCounter(Options());
        var now = Origin;
        var verdicts = new List<ItemVerdict>();

        // Two eggs a third of the frame apart, moving together; the second small.
        for (var x = 0.05; x <= 0.95; x += 0.03)
        {
            var frame = new List<Detection> { Egg(x) };
            if (x - 0.33 > 0.02) frame.Add(Egg(x - 0.33, widthMm: 39));

            verdicts.AddRange(counter.Observe(now, frame));
            now += FrameStep;
        }

        for (var x = 0.65; x <= 0.95; x += 0.03)
        {
            verdicts.AddRange(counter.Observe(now, [Egg(x, widthMm: 39)]));
            now += FrameStep;
        }

        Assert.Equal(2, verdicts.Count);
        Assert.NotEqual(verdicts[0].ItemId, verdicts[1].ItemId);
        Assert.Equal(InspectionStatus.Ok, verdicts[0].Status);
        Assert.Equal(InspectionStatus.Nok, verdicts[1].Status);
    }

    [Fact]
    public void TheOtherDirection_JudgesAtTheMirroredLine()
    {
        var counter = new LineCounter(Options(o => o.Direction = LineDirection.RightToLeft));
        var now = Origin;

        var verdicts = Run(counter, ref now, 0.95, 0.05, -0.03, (_, x) => Egg(x));

        Assert.Single(verdicts);
    }

    [Fact]
    public void ABriefStall_KeepsTheSameItem()
    {
        // The pipeline hiccups for 0.3 s; the egg is still the egg it was.
        var counter = new LineCounter(Options());
        var now = Origin;

        counter.Observe(now, [Egg(0.40)]);
        now += FrameStep;
        counter.Observe(now, [Egg(0.43)]);
        now += TimeSpan.FromMilliseconds(300);

        var verdicts = counter.Observe(now, [Egg(0.52)]);

        Assert.Single(verdicts);
    }

    [Fact]
    public void AnItemGoneLongerThanMaxAbsence_IsDropped()
    {
        var counter = new LineCounter(Options());
        var now = Origin;

        counter.Observe(now, [Egg(0.40)]);
        now += FrameStep;
        counter.Observe(now, [Egg(0.43)]);
        now += TimeSpan.FromSeconds(2);

        // Same place, two seconds later: a new item, seen once, not yet crossing.
        Assert.Empty(counter.Observe(now, [Egg(0.52)]));
        Assert.Equal(1, counter.ActiveItems);
    }

    [Fact]
    public void ReplayingTheSameFrames_GivesTheSameVerdicts()
    {
        static List<ItemVerdict> Once()
        {
            var counter = new LineCounter(Options());
            var now = Origin;
            return Run(counter, ref now, 0.05, 0.95, 0.03, (frame, x) => Egg(x, widthMm: 40 + frame % 5));
        }

        var first = Once();
        var second = Once();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(v => (v.ItemId, v.At, v.Status, v.Reason)),
                     second.Select(v => (v.ItemId, v.At, v.Status, v.Reason)));
    }

    [Fact]
    public void WithNoLimitsAndNoRejectClasses_ItOnlyCounts()
    {
        var counter = new LineCounter(Options(o =>
        {
            o.Limits = [];
            o.RejectClasses = [];
        }));
        var now = Origin;

        var verdict = Assert.Single(Run(counter, ref now, 0.05, 0.95, 0.03, (_, x) => Egg(x, widthMm: null)));

        Assert.Equal(InspectionStatus.Ok, verdict.Status);
    }
}
