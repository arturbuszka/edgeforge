using EdgeForge.Decision;
using Xunit;

namespace EdgeForge.Core.Tests;

public class ItemRuleTests
{
    private static readonly ItemRule Rule = new(
        [
            new LimitOptions { Measurement = "width_mm", Min = 42, Reason = "Undersize" },
            new LimitOptions { Measurement = "length_mm", Max = 66 }
        ],
        ["egg_cracked"]);

    private static Detection Egg(string className = "egg", double? width = 44, double? length = 57)
    {
        var measurements = new Dictionary<string, double>();
        if (width is { } w) measurements["width_mm"] = w;
        if (length is { } l) measurements["length_mm"] = l;

        return new Detection(className, 0.9, new BoundingBox(0.4, 0.4, 0.1, 0.1), measurements);
    }

    [Fact]
    public void WithinEveryLimit_IsOk() =>
        Assert.Equal(InspectionStatus.Ok, Rule.Judge(Egg()).Status);

    [Fact]
    public void BelowMin_IsNok_WithTheConfiguredReason()
    {
        var judgement = Rule.Judge(Egg(width: 40));

        Assert.Equal(InspectionStatus.Nok, judgement.Status);
        Assert.Equal("Undersize", judgement.Reason);
    }

    [Fact]
    public void ALimitWithNoReason_DescribesItself()
    {
        var judgement = Rule.Judge(Egg(length: 70));

        Assert.Equal(InspectionStatus.Nok, judgement.Status);
        Assert.Equal("length_mm 70.0 > 66", judgement.Reason);
    }

    [Fact]
    public void AMissingMeasurement_IsUnknown_NotOk()
    {
        var judgement = Rule.Judge(Egg(width: null));

        Assert.Equal(InspectionStatus.Unknown, judgement.Status);
    }

    [Fact]
    public void NoMeasurementsAtAll_IsUnknown()
    {
        var bare = new Detection("egg", 0.9, new BoundingBox(0, 0, 0.1, 0.1));

        Assert.Equal(InspectionStatus.Unknown, Rule.Judge(bare).Status);
    }

    [Fact]
    public void ARejection_OutranksAMissingMeasurement()
    {
        // Known to be too long, width unknown: that egg is out, not "unknown".
        var judgement = Rule.Judge(Egg(width: null, length: 70));

        Assert.Equal(InspectionStatus.Nok, judgement.Status);
    }

    [Fact]
    public void ARejectClass_IsNok_AtAnySize()
    {
        var judgement = Rule.Judge(Egg(className: "egg_cracked"));

        Assert.Equal(InspectionStatus.Nok, judgement.Status);
        Assert.Equal("egg_cracked", judgement.Reason);
    }

    [Fact]
    public void SeveralRejections_AreAllNamed()
    {
        var judgement = Rule.Judge(Egg(className: "egg_cracked", width: 40));

        Assert.Equal("egg_cracked, Undersize", judgement.Reason);
    }

    [Fact]
    public void ItemRuleInspector_EmptyBelt_IsOk()
    {
        // Gaps between items are the normal state of a line.
        var result = new ItemRuleInspector(Rule).Inspect(
            new Frame("F", DateTimeOffset.UtcNow, 640, 360), [], TimeSpan.Zero);

        Assert.Equal(InspectionStatus.Ok, result.Status);
    }

    [Fact]
    public void ItemRuleInspector_OneBadItemInView_IsNokForTheFrame()
    {
        var result = new ItemRuleInspector(Rule).Inspect(
            new Frame("F", DateTimeOffset.UtcNow, 640, 360),
            [Egg(), Egg(width: 39)],
            TimeSpan.Zero);

        Assert.Equal(InspectionStatus.Nok, result.Status);
        Assert.Equal("1/2 out of spec: Undersize", result.Reason);
    }
}
