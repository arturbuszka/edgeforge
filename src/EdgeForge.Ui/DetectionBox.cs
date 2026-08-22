using Avalonia.Media;
using EdgeForge.Core;
using EdgeForge.Vision;

namespace EdgeForge.Ui;

/// <summary>One detection positioned over the video, in control coordinates.</summary>
public sealed class DetectionBox : ViewModelBase
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private string _label = "";
    private IBrush _stroke = Brushes.Lime;

    public double X { get => _x; set => SetField(ref _x, value); }
    public double Y { get => _y; set => SetField(ref _y, value); }
    public double Width { get => _width; set => SetField(ref _width, value); }
    public double Height { get => _height; set => SetField(ref _height, value); }
    public string Label { get => _label; set => SetField(ref _label, value); }
    public IBrush Stroke { get => _stroke; set => SetField(ref _stroke, value); }

    /// <summary>
    /// Reuses this instance for a new detection rather than allocating a fresh one.
    /// The overlay is rebuilt at camera framerate, so recycling keeps the UI thread
    /// out of the allocator and avoids a full ItemsControl teardown per frame.
    /// </summary>
    public void Update(Detection detection, ProjectedBox box)
    {
        X = box.X;
        Y = box.Y;
        Width = box.Width;
        Height = box.Height;
        Label = $"{detection.ClassName} {detection.Confidence:P0}";
        Stroke = ConfidenceBrush(detection.Confidence);
    }

    /// <summary>
    /// Colour tracks confidence so a weak detection is obvious at a glance while
    /// the camera is being aimed at the bowls.
    /// </summary>
    private static IBrush ConfidenceBrush(double confidence) => confidence switch
    {
        >= 0.75 => Brushes.Lime,
        >= 0.50 => Brushes.Gold,
        _ => Brushes.OrangeRed
    };
}
