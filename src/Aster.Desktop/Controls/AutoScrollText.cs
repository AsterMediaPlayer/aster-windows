using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Aster.Desktop.Controls;

/// <summary>
/// Text in a window of a few lines. When it runs longer, it waits, drifts
/// slowly up to its last line, holds there, and glides back to the top, over
/// and over while it's on screen; the bottom edge fades while there's more
/// below. The Apple app's AutoScrollingText, for the pause screen's
/// description. Its font comes from TextElement's inherited properties.
/// </summary>
public sealed class AutoScrollText : Decorator
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<AutoScrollText, string?>(nameof(Text));

    public static readonly StyledProperty<int> VisibleLinesProperty =
        AvaloniaProperty.Register<AutoScrollText, int>(nameof(VisibleLines), 4);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int VisibleLines
    {
        get => GetValue(VisibleLinesProperty);
        set => SetValue(VisibleLinesProperty, value);
    }

    /// <summary>How fast it reads up, in pixels a second: slow enough to read along.</summary>
    private const double Speed = 18;
    private const double HoldSeconds = 3;
    private const double ReturnSeconds = 0.8;

    private readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TranslateTransform shift = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch clock = new();
    private double overflow;

    public AutoScrollText()
    {
        ClipToBounds = true;
        text.RenderTransform = shift;
        Child = text;
        timer.Tick += (_, _) => Step();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            text.Text = change.GetNewValue<string?>();
            Restart();
        }
    }

    private double Window => Math.Ceiling(text.FontSize * 1.35 * VisibleLines);

    protected override Size MeasureOverride(Size availableSize)
    {
        text.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var full = text.DesiredSize;
        var overflowNow = Math.Max(full.Height - Window, 0);
        if (Math.Abs(overflowNow - overflow) > 0.5)
        {
            overflow = overflowNow;
            Restart();
        }
        return new Size(full.Width, Math.Min(full.Height, Window));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        text.Arrange(new Rect(0, 0, finalSize.Width, Math.Max(text.DesiredSize.Height, finalSize.Height)));
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Back to the top, and moving again if it's too long for its window.</summary>
    private void Restart()
    {
        shift.Y = 0;
        clock.Restart();
        OpacityMask = overflow > 1 ? Fade(bottom: true) : null;
        if (overflow > 1 && VisualRoot is not null) timer.Start();
        else timer.Stop();
    }

    /// <summary>A hold, a drift up, a hold, a glide back: one cycle, then again.</summary>
    private void Step()
    {
        var drift = overflow / Speed;
        var t = clock.Elapsed.TotalSeconds;
        double y;
        if (t < HoldSeconds) y = 0;
        else if (t < HoldSeconds + drift) y = (t - HoldSeconds) * Speed;
        else if (t < 2 * HoldSeconds + drift) y = overflow;
        else if (t < 2 * HoldSeconds + drift + ReturnSeconds)
        {
            var back = (t - 2 * HoldSeconds - drift) / ReturnSeconds;
            y = overflow * (1 - (1 - Math.Cos(Math.PI * back)) / 2);
        }
        else
        {
            clock.Restart();
            y = 0;
        }
        shift.Y = -y;
        // An edge fades only while there's more text past it.
        OpacityMask = Fade(top: y > 1, bottom: overflow - y > 1);
    }

    private static LinearGradientBrush? Fade(bool top = false, bool bottom = false)
    {
        if (!top && !bottom) return null;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(top ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, top ? 0.14 : 0),
                new GradientStop(Colors.Black, bottom ? 0.86 : 1),
                new GradientStop(bottom ? Colors.Transparent : Colors.Black, 1),
            },
        };
    }
}
