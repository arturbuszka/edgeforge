using EdgeForge.Core;

namespace EdgeForge.Vision;

/// <summary>Where a normalised box lands on screen, in device-independent pixels.</summary>
public readonly record struct ProjectedBox(double X, double Y, double Width, double Height);

/// <summary>
/// Maps normalised detection boxes onto the rectangle a control actually paints
/// the frame into.
/// </summary>
/// <remarks>
/// Displaying a 16:9 frame in a 4:3 control letterboxes it: the image is centred
/// and the leftover space stays empty. Scaling boxes by the control size instead
/// of the painted rectangle puts every box slightly off the animal, and the error
/// grows with the aspect mismatch — so the offset is computed here, once, against
/// the same Uniform rule the Image control uses.
/// </remarks>
public static class BoxProjection
{
    /// <summary>
    /// Projects a 0..1 box onto a control of the given size showing a frame of the
    /// given size under <c>Stretch="Uniform"</c>. Returns a zero-size box when
    /// either the frame or the control has no area.
    /// </summary>
    public static ProjectedBox Project(
        BoundingBox box,
        int frameWidth,
        int frameHeight,
        double controlWidth,
        double controlHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0) return default;
        if (controlWidth <= 0 || controlHeight <= 0) return default;

        // Uniform fits the whole frame inside the control: the tighter axis wins.
        var scale = Math.Min(controlWidth / frameWidth, controlHeight / frameHeight);

        var paintedWidth = frameWidth * scale;
        var paintedHeight = frameHeight * scale;

        // Uniform also centres what it paints; that offset is the letterbox bar.
        var offsetX = (controlWidth - paintedWidth) / 2.0;
        var offsetY = (controlHeight - paintedHeight) / 2.0;

        return new ProjectedBox(
            offsetX + box.X * paintedWidth,
            offsetY + box.Y * paintedHeight,
            box.Width * paintedWidth,
            box.Height * paintedHeight);
    }
}
