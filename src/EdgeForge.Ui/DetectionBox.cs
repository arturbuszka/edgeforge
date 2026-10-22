using System.Globalization;
using Avalonia.Media;
using EdgeForge.Core;
using EdgeForge.Decision;
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
    /// <param name="rule">
    /// The scenario's acceptance rule, when it has one. With a rule the box shows
    /// the measurement and is coloured by verdict; without one, by confidence.
    /// </param>
    public void Update(Detection detection, ProjectedBox box, ItemRule? rule = null)
    {
        X = box.X;
        Y = box.Y;
        Width = box.Width;
        Height = box.Height;

        if (rule is null)
        {
            Label = $"{detection.ClassName} {detection.Confidence:P0}";
            Stroke = ConfidenceBrush(detection.Confidence);
            return;
        }

        Label = MeasuredLabel(detection);
        Stroke = rule.Judge(detection).Status switch
        {
            InspectionStatus.Ok => Brushes.Lime,
            InspectionStatus.Nok => Brushes.OrangeRed,
            _ => Brushes.Gray
        };
    }

    /// <summary>"egg 57.1×43.9 mm", or the class alone when nothing was measured.</summary>
    private static string MeasuredLabel(Detection detection)
    {
        var m = detection.Measurements;
        if (m is null) return detection.ClassName;

        if (m.TryGetValue(MeasurementNames.LengthMm, out var length)
            && m.TryGetValue(MeasurementNames.WidthMm, out var width))
        {
            return string.Create(
                CultureInfo.InvariantCulture, $"{detection.ClassName} {length:F1}×{width:F1} mm");
        }

        if (m.TryGetValue(MeasurementNames.LengthPx, out var lengthPx)
            && m.TryGetValue(MeasurementNames.WidthPx, out var widthPx))
        {
            return string.Create(
                CultureInfo.InvariantCulture, $"{detection.ClassName} {lengthPx:F0}×{widthPx:F0} px");
        }

        return detection.ClassName;
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
