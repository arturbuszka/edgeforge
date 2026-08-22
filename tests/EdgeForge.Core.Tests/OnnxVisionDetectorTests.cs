using EdgeForge.Vision;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// Postprocessing is where a YOLOv8 port actually goes wrong: the output is
/// transposed relative to YOLOX and there is no objectness channel. Those two
/// steps are tested here against hand-built tensors, with no model on disk and
/// no hardware. The rest of the detector needs real weights and belongs in
/// manual verification.
/// </summary>
public class OnnxVisionDetectorTests
{
    private sealed class FakeFrameImage : IFrameImage
    {
        public int Width => 640;
        public int Height => 480;
        public void Dispose() { }
    }

    /// <summary>
    /// Builds a YOLOv8-shaped output [1, 4 + numClasses, numAnchors]. Boxes are
    /// cx/cy/w/h in network pixels, exactly as the export emits them.
    /// </summary>
    private static DenseTensor<float> BuildOutput(
        int numClasses,
        params (float Cx, float Cy, float W, float H, int Cls, float Score)[] anchors)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 4 + numClasses, anchors.Length });

        for (var i = 0; i < anchors.Length; i++)
        {
            var a = anchors[i];
            tensor[0, 0, i] = a.Cx;
            tensor[0, 1, i] = a.Cy;
            tensor[0, 2, i] = a.W;
            tensor[0, 3, i] = a.H;
            tensor[0, 4 + a.Cls, i] = a.Score;
        }

        return tensor;
    }

    [Fact]
    public void FrameWithoutImage_HasNoReadablePixels()
    {
        // The synthetic demo sequence builds frames with no pixels at all, and
        // DetectAsync turns a null here into an empty list rather than a crash.
        var frame = new Frame("BOX-001", DateTimeOffset.UtcNow, 1920, 1080);

        Assert.Null(OnnxVisionDetector.ReadablePixels(frame));
    }

    [Fact]
    public void NonMatFrameImage_HasNoReadablePixels()
    {
        // An image from some other adapter is an absence of observation, not a fault.
        using var image = new FakeFrameImage();
        var frame = new Frame(
            "CAM-000001", DateTimeOffset.UtcNow, image.Width, image.Height, image);

        Assert.Null(OnnxVisionDetector.ReadablePixels(frame));
    }

    [Fact]
    public void MatFrameImage_ExposesItsBufferWithoutCopying()
    {
        using var mat = new OpenCvSharp.Mat(8, 8, OpenCvSharp.MatType.CV_8UC3);
        using var image = new MatFrameImage(mat);
        var frame = new Frame(
            "CAM-000002", DateTimeOffset.UtcNow, image.Width, image.Height, image);

        // Same instance, not a copy: this is why MatFrameImage exists.
        Assert.Same(mat, OnnxVisionDetector.ReadablePixels(frame));
    }

    [Fact]
    public void Decode_ReadsChannelsFromTheMiddleDimension()
    {
        // Transposed layout: [1, channels, anchors]. Reading it as YOLOX would
        // ([1, anchors, channels]) picks up entirely different numbers.
        var output = BuildOutput(
            numClasses: 80,
            (Cx: 100f, Cy: 200f, W: 40f, H: 60f, Cls: 15, Score: 0.9f));

        var candidates = OnnxVisionDetector.DecodeTransposed(output, 0.25f, 15);

        var (tlbr, score, cls) = Assert.Single(candidates);
        Assert.Equal(15, cls);
        Assert.Equal(0.9f, score, 5);
        Assert.Equal(80.0, tlbr[0], 5);   // cx - w/2
        Assert.Equal(170.0, tlbr[1], 5);  // cy - h/2
        Assert.Equal(120.0, tlbr[2], 5);  // cx + w/2
        Assert.Equal(230.0, tlbr[3], 5);  // cy + h/2
    }

    [Fact]
    public void Decode_ScoreIsTheBestClassAlone_NoObjectness()
    {
        // A YOLOX-style obj*cls would need a channel 4 that YOLOv8 does not emit;
        // here channel 4 is class 0 and must be treated as a class score.
        var output = BuildOutput(
            numClasses: 80,
            (Cx: 100f, Cy: 100f, W: 20f, H: 20f, Cls: 15, Score: 0.42f));

        var candidates = OnnxVisionDetector.DecodeTransposed(output, 0.25f, 15);

        Assert.Equal(0.42f, Assert.Single(candidates).Score, 5);
    }

    [Fact]
    public void Decode_BoxesAreAlreadyInNetworkPixels()
    {
        // No (xy + grid) * stride and no exp(wh) * stride: a width of 40 stays 40.
        var output = BuildOutput(
            numClasses: 80,
            (Cx: 320f, Cy: 320f, W: 40f, H: 40f, Cls: 15, Score: 0.8f));

        var (tlbr, _, _) = Assert.Single(
            OnnxVisionDetector.DecodeTransposed(output, 0.25f, 15));

        Assert.Equal(40.0, tlbr[2] - tlbr[0], 5);
        Assert.Equal(40.0, tlbr[3] - tlbr[1], 5);
    }

    [Fact]
    public void Decode_DropsAnchorsBelowTheScoreThreshold()
    {
        var output = BuildOutput(
            numClasses: 80,
            (Cx: 100f, Cy: 100f, W: 20f, H: 20f, Cls: 15, Score: 0.90f),
            (Cx: 300f, Cy: 300f, W: 20f, H: 20f, Cls: 15, Score: 0.10f));

        var candidates = OnnxVisionDetector.DecodeTransposed(output, 0.25f, 15);

        Assert.Equal(0.90f, Assert.Single(candidates).Score, 5);
    }

    [Fact]
    public void Decode_KeepsOnlyTheRequestedClass()
    {
        var output = BuildOutput(
            numClasses: 80,
            (Cx: 100f, Cy: 100f, W: 20f, H: 20f, Cls: 15, Score: 0.9f),
            (Cx: 300f, Cy: 300f, W: 20f, H: 20f, Cls: 0, Score: 0.9f),
            (Cx: 500f, Cy: 500f, W: 20f, H: 20f, Cls: 14, Score: 0.9f));

        Assert.Equal(15, Assert.Single(
            OnnxVisionDetector.DecodeTransposed(output, 0.25f, 15)).Cls);
        Assert.Equal(0, Assert.Single(
            OnnxVisionDetector.DecodeTransposed(output, 0.25f, 0)).Cls);
        Assert.Equal(14, Assert.Single(
            OnnxVisionDetector.DecodeTransposed(output, 0.25f, 14)).Cls);
    }

    [Fact]
    public void Decode_NegativeClassIdKeepsEveryClass()
    {
        var output = BuildOutput(
            numClasses: 80,
            (Cx: 100f, Cy: 100f, W: 20f, H: 20f, Cls: 15, Score: 0.9f),
            (Cx: 300f, Cy: 300f, W: 20f, H: 20f, Cls: 0, Score: 0.9f));

        Assert.Equal(2, OnnxVisionDetector.DecodeTransposed(output, 0.25f, -1).Count);
    }

    [Fact]
    public void Decode_PicksTheHighestScoringClassPerAnchor()
    {
        var tensor = new DenseTensor<float>(new[] { 1, 84, 1 });
        tensor[0, 0, 0] = 100f;
        tensor[0, 1, 0] = 100f;
        tensor[0, 2, 0] = 20f;
        tensor[0, 3, 0] = 20f;
        tensor[0, 4 + 15, 0] = 0.80f;  // cat
        tensor[0, 4 + 16, 0] = 0.30f;  // dog

        // Best class wins, so asking for the runner-up class returns nothing.
        Assert.Equal(15, Assert.Single(
            OnnxVisionDetector.DecodeTransposed(tensor, 0.25f, 15)).Cls);
        Assert.Empty(OnnxVisionDetector.DecodeTransposed(tensor, 0.25f, 16));
    }

    [Fact]
    public void Decode_RejectsAnOutputThatIsNotThreeDimensional()
    {
        var flat = new DenseTensor<float>(new[] { 1, 84 });

        Assert.Throws<InvalidOperationException>(
            () => OnnxVisionDetector.DecodeTransposed(flat, 0.25f, 15));
    }

    [Fact]
    public void Decode_RejectsAnOutputWithNoClassChannels()
    {
        var boxesOnly = new DenseTensor<float>(new[] { 1, 4, 10 });

        Assert.Throws<InvalidOperationException>(
            () => OnnxVisionDetector.DecodeTransposed(boxesOnly, 0.25f, 15));
    }

    [Fact]
    public void Nms_SuppressesOverlappingBoxesAndKeepsTheBestScore()
    {
        var candidates = new List<(double[] Tlbr, float Score, int Cls)>
        {
            (new[] { 100.0, 100.0, 200.0, 200.0 }, 0.7f, 15),
            (new[] { 105.0, 105.0, 205.0, 205.0 }, 0.9f, 15),
        };

        var kept = OnnxVisionDetector.NonMaxSuppression(candidates, 0.45f);

        Assert.Equal(0.9f, Assert.Single(kept).Score, 5);
    }

    [Fact]
    public void Nms_KeepsSeparateObjects()
    {
        // Two cats standing apart must stay two cats, or the count rule is wrong.
        var candidates = new List<(double[] Tlbr, float Score, int Cls)>
        {
            (new[] { 0.0, 0.0, 50.0, 50.0 }, 0.9f, 15),
            (new[] { 400.0, 400.0, 450.0, 450.0 }, 0.8f, 15),
        };

        var kept = OnnxVisionDetector.NonMaxSuppression(candidates, 0.45f);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Nms_IsPerClass_OverlapAcrossClassesSurvives()
    {
        var candidates = new List<(double[] Tlbr, float Score, int Cls)>
        {
            (new[] { 100.0, 100.0, 200.0, 200.0 }, 0.9f, 15),
            (new[] { 100.0, 100.0, 200.0, 200.0 }, 0.8f, 16),
        };

        var kept = OnnxVisionDetector.NonMaxSuppression(candidates, 0.45f);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Nms_OnNothingYieldsNothing()
    {
        var kept = OnnxVisionDetector.NonMaxSuppression(
            new List<(double[] Tlbr, float Score, int Cls)>(), 0.45f);

        Assert.Empty(kept);
    }

    [Fact]
    public void Iou_OfIdenticalBoxesIsOne_AndOfDisjointBoxesIsZero()
    {
        var a = new[] { 0.0, 0.0, 10.0, 10.0 };
        var b = new[] { 100.0, 100.0, 110.0, 110.0 };

        Assert.Equal(1.0, OnnxVisionDetector.Iou(a, a), 6);
        Assert.Equal(0.0, OnnxVisionDetector.Iou(a, b), 6);
    }
}
