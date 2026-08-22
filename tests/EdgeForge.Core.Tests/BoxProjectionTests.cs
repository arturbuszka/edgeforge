using EdgeForge.Core;
using EdgeForge.Vision;
using Xunit;

namespace EdgeForge.Core.Tests;

/// <summary>
/// The overlay is only trustworthy if a box lands exactly where the pixels do.
/// Uniform stretch letterboxes the frame, so scaling by the control size instead
/// of the painted rectangle puts every box off the animal.
/// </summary>
public class BoxProjectionTests
{
    [Fact]
    public void MatchingAspect_FillsTheControl()
    {
        // 2:1 frame in a 2:1 control: no letterbox, so a full-frame box is the control.
        var box = new BoundingBox(0, 0, 1, 1);

        var projected = BoxProjection.Project(box, 640, 320, 1280, 640);

        Assert.Equal(0, projected.X, 6);
        Assert.Equal(0, projected.Y, 6);
        Assert.Equal(1280, projected.Width, 6);
        Assert.Equal(640, projected.Height, 6);
    }

    [Fact]
    public void WideFrameInTallControl_LeavesHorizontalBars()
    {
        // 2:1 frame in a 1:1 control: width binds, so bars appear top and bottom.
        var box = new BoundingBox(0, 0, 1, 1);

        var projected = BoxProjection.Project(box, 640, 320, 400, 400);

        Assert.Equal(0, projected.X, 6);
        Assert.Equal(400, projected.Width, 6);
        Assert.Equal(200, projected.Height, 6);
        Assert.Equal(100, projected.Y, 6); // (400 - 200) / 2
    }

    [Fact]
    public void TallFrameInWideControl_LeavesVerticalBars()
    {
        // 1:2 frame in a 1:1 control: height binds, so bars appear left and right.
        var box = new BoundingBox(0, 0, 1, 1);

        var projected = BoxProjection.Project(box, 320, 640, 400, 400);

        Assert.Equal(0, projected.Y, 6);
        Assert.Equal(400, projected.Height, 6);
        Assert.Equal(200, projected.Width, 6);
        Assert.Equal(100, projected.X, 6); // (400 - 200) / 2
    }

    [Fact]
    public void CentreBox_StaysCentredDespiteLetterbox()
    {
        // A box in the middle of the frame must stay in the middle of the control,
        // whichever way the bars fall.
        var box = new BoundingBox(0.25, 0.25, 0.5, 0.5);

        var projected = BoxProjection.Project(box, 640, 320, 400, 400);

        Assert.Equal(200, projected.X + (projected.Width / 2), 6);
        Assert.Equal(200, projected.Y + (projected.Height / 2), 6);
    }

    [Theory]
    [InlineData(0, 100, 100, 100)]   // no frame width
    [InlineData(100, 0, 100, 100)]   // no frame height
    [InlineData(100, 100, 0, 100)]   // control not laid out yet
    [InlineData(100, 100, 100, 0)]
    public void DegenerateSizes_ProjectToNothing(
        int frameWidth, int frameHeight, double controlWidth, double controlHeight)
    {
        // A control mid-layout reports zero; that must not produce NaN or Infinity
        // in a Canvas.Left binding.
        var projected = BoxProjection.Project(
            new BoundingBox(0, 0, 1, 1), frameWidth, frameHeight, controlWidth, controlHeight);

        Assert.Equal(0, projected.Width);
        Assert.Equal(0, projected.Height);
    }
}
