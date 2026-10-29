using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SlopstackLabs.Slaw.Setup;

/// <summary>
/// Two turntables and a mixer. While setup runs the tonearms drop, the records
/// spin up, the meters bounce and the crossfader tracks progress. Uninstalling
/// backspins them, as is tradition.
/// </summary>
internal sealed class DjDeck : StackPanel
{
    private readonly Turntable left = new(rpm: 33.3, labelAngle: 0);
    private readonly Turntable right = new(rpm: 45, labelAngle: 140);
    private readonly Mixer mixer = new();
    private TimeSpan lastFrame = TimeSpan.Zero;

    public DjDeck()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(left);
        Children.Add(mixer);
        Children.Add(right);

        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public void Play(bool reverse)
    {
        left.Play(reverse ? -1 : 1);
        right.Play(reverse ? -1 : 1);
        mixer.Live = true;
    }

    public void Stop()
    {
        left.Stop();
        right.Stop();
        mixer.Live = false;
    }

    public void SetProgress(double fraction) => mixer.Crossfade = fraction;

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == lastFrame)
        {
            return;
        }

        var seconds = lastFrame == TimeSpan.Zero ? 0 : Math.Min(0.1, (now - lastFrame).TotalSeconds);
        lastFrame = now;
        left.Advance(seconds);
        right.Advance(seconds);
        mixer.Advance(seconds);
    }

    /// <summary>Eases a value toward a target at the given rate per second.</summary>
    internal static double Approach(double current, double target, double rate, double seconds) =>
        current + (target - current) * (1 - Math.Exp(-rate * seconds));
}

internal sealed class Turntable : Canvas
{
    private const double PlateWidth = 176;
    private const double PlateHeight = 150;
    private const double RecordSize = 128;
    private const double RecordLeft = 8;
    private const double RecordTop = 11;
    private const double PivotX = 152;
    private const double PivotY = 24;
    private const double ArmRestAngle = -4;
    private const double ArmPlayAngle = 27;

    private readonly double degreesPerSecond;
    private readonly RotateTransform recordRotation;
    private readonly RotateTransform armRotation;
    private readonly Ellipse led;
    private double speed;
    private double targetSpeed;
    private double armAngle = ArmRestAngle;
    private double targetArmAngle = ArmRestAngle;

    public Turntable(double rpm, double labelAngle)
    {
        degreesPerSecond = rpm * 6;
        Width = PlateWidth;
        Height = PlateHeight;

        Children.Add(new Border
        {
            Width = PlateWidth,
            Height = PlateHeight,
            CornerRadius = new CornerRadius(12),
            Background = new LinearGradientBrush(SetupTheme.Color("#22222f"), SetupTheme.Color("#16161f"), 90),
            BorderBrush = SetupTheme.Border,
            BorderThickness = new Thickness(1),
        });

        // Platter
        Place(new Ellipse { Width = RecordSize + 10, Height = RecordSize + 10, Fill = SetupTheme.Solid("#06060a") },
            RecordLeft - 5, RecordTop - 5);

        // Record, which is the only thing that rotates
        recordRotation = new RotateTransform(labelAngle);
        var record = new Grid
        {
            Width = RecordSize,
            Height = RecordSize,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = recordRotation,
        };
        record.Children.Add(new Ellipse
        {
            Fill = new RadialGradientBrush(SetupTheme.Color("#1b1b25"), SetupTheme.Color("#09090d")),
        });
        for (var radius = 61.0; radius > 26; radius -= 4.5)
        {
            record.Children.Add(new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = SetupTheme.Solid("#ffffff", radius % 9 < 4.5 ? (byte)14 : (byte)7),
                StrokeThickness = 1,
            });
        }

        var label = new Grid { Width = 46, Height = 46 };
        label.Children.Add(new Ellipse { Fill = SetupTheme.AccentGradient });
        label.Children.Add(new TextBlock
        {
            Text = "slaw",
            FontFamily = SetupTheme.DisplayFont,
            FontWeight = FontWeights.Bold,
            FontSize = 10,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 0, 0),
        });
        label.Children.Add(new Rectangle
        {
            Width = 16,
            Height = 2,
            RadiusX = 1,
            RadiusY = 1,
            Fill = SetupTheme.Solid("#ffffff", 150),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10),
        });
        label.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = SetupTheme.Solid("#0b0b10") });
        record.Children.Add(label);
        Place(record, RecordLeft, RecordTop);

        // A stationary gleam sells the rotation underneath it
        Place(new Ellipse
        {
            Width = RecordSize,
            Height = RecordSize,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.Transparent, 0.30),
                    new(Color.FromArgb(22, 255, 255, 255), 0.47),
                    new(Colors.Transparent, 0.56),
                    new(Colors.Transparent, 1),
                },
                new Point(0, 0), new Point(1, 1)),
        }, RecordLeft, RecordTop);

        // Pitch fader
        Place(new Rectangle { Width = 4, Height = 46, RadiusX = 2, RadiusY = 2, Fill = SetupTheme.Solid("#0b0b10") }, 166, 66);
        Place(new Rectangle { Width = 12, Height = 6, RadiusX = 2, RadiusY = 2, Fill = SetupTheme.Solid("#8a8aa3") }, 162, 86);

        // Power light
        led = new Ellipse { Width = 6, Height = 6, Fill = SetupTheme.TextDim };
        Place(led, 165, 134);

        // Tonearm, rotating about its pivot
        armRotation = new RotateTransform(ArmRestAngle, PivotX, PivotY);
        var arm = new Canvas { Width = PlateWidth, Height = PlateHeight, RenderTransform = armRotation, IsHitTestVisible = false };
        arm.Children.Add(new Line
        {
            X1 = PivotX,
            Y1 = PivotY - 12,
            X2 = PivotX,
            Y2 = PivotY + 78,
            Stroke = SetupTheme.Solid("#c9c9d6"),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        arm.Children.Add(new Line
        {
            X1 = PivotX,
            Y1 = PivotY + 78,
            X2 = PivotX - 6,
            Y2 = PivotY + 90,
            Stroke = SetupTheme.Solid("#c9c9d6"),
            StrokeThickness = 3,
            StrokeEndLineCap = PenLineCap.Round,
        });
        var headshell = new Rectangle
        {
            Width = 9,
            Height = 15,
            RadiusX = 2,
            RadiusY = 2,
            Fill = SetupTheme.Solid("#ededf2"),
            RenderTransform = new RotateTransform(28, 4.5, 7.5),
        };
        SetLeft(headshell, PivotX - 12);
        SetTop(headshell, PivotY + 86);
        arm.Children.Add(headshell);
        var counterweight = new Ellipse { Width = 13, Height = 13, Fill = SetupTheme.Solid("#4a4a5e") };
        SetLeft(counterweight, PivotX - 6.5);
        SetTop(counterweight, PivotY - 20);
        arm.Children.Add(counterweight);
        Children.Add(arm);

        // Pivot base drawn above the arm
        Place(new Ellipse { Width = 20, Height = 20, Fill = SetupTheme.Solid("#2a2a3a"), Stroke = SetupTheme.Solid("#3a3a4e") }, PivotX - 10, PivotY - 10);
        Place(new Ellipse { Width = 8, Height = 8, Fill = SetupTheme.Accent }, PivotX - 4, PivotY - 4);
    }

    public void Play(int direction)
    {
        targetSpeed = degreesPerSecond * direction;
        targetArmAngle = ArmPlayAngle;
        led.Fill = SetupTheme.Playhead;
    }

    public void Stop()
    {
        targetSpeed = 0;
        targetArmAngle = ArmRestAngle;
        led.Fill = SetupTheme.TextDim;
    }

    public void Advance(double seconds)
    {
        // Platters take a moment to reach speed, like the real thing
        speed = DjDeck.Approach(speed, targetSpeed, targetSpeed == 0 ? 1.4 : 1.8, seconds);
        recordRotation.Angle = (recordRotation.Angle + speed * seconds) % 360;
        armAngle = DjDeck.Approach(armAngle, targetArmAngle, 4, seconds);
        armRotation.Angle = armAngle;
    }

    private void Place(UIElement element, double left, double top)
    {
        SetLeft(element, left);
        SetTop(element, top);
        Children.Add(element);
    }
}

internal sealed class Mixer : Border
{
    private const int Segments = 9;
    private const double FaderTravel = 44;
    private static readonly Brush Unlit = SetupTheme.Solid("#0b0b10");

    private readonly Rectangle[,] meters = new Rectangle[2, Segments];
    private readonly double[] levels = new double[2];
    private readonly Random random = new();
    private readonly Border knob;
    private double crossfade;
    private double displayedCrossfade;
    private double meterClock;

    public Mixer()
    {
        Width = 72;
        Height = 150;
        Margin = new Thickness(10, 0, 10, 0);
        CornerRadius = new CornerRadius(12);
        Background = new LinearGradientBrush(SetupTheme.Color("#22222f"), SetupTheme.Color("#16161f"), 90);
        BorderBrush = SetupTheme.Border;
        BorderThickness = new Thickness(1);

        var canvas = new Canvas();
        for (var channel = 0; channel < 2; channel++)
        {
            for (var segment = 0; segment < Segments; segment++)
            {
                var cell = new Rectangle { Width = 10, Height = 6, RadiusX = 1.5, RadiusY = 1.5, Fill = Unlit };
                Canvas.SetLeft(cell, 20 + channel * 22);
                Canvas.SetTop(cell, 92 - segment * 8.5);
                canvas.Children.Add(cell);
                meters[channel, segment] = cell;
            }
        }

        var track = new Rectangle { Width = FaderTravel + 12, Height = 4, RadiusX = 2, RadiusY = 2, Fill = SetupTheme.Solid("#0b0b10") };
        Canvas.SetLeft(track, 7);
        Canvas.SetTop(track, 124);
        canvas.Children.Add(track);

        knob = new Border { Width = 12, Height = 16, CornerRadius = new CornerRadius(3), Background = SetupTheme.Solid("#c9c9d6") };
        knob.Child = new Rectangle { Width = 2, Height = 10, Fill = SetupTheme.Solid("#4a4a5e") };
        Canvas.SetLeft(knob, 7);
        Canvas.SetTop(knob, 118);
        canvas.Children.Add(knob);

        Child = canvas;
    }

    public bool Live { get; set; }

    public double Crossfade
    {
        set => crossfade = Math.Max(0, Math.Min(1, value));
    }

    public void Advance(double seconds)
    {
        displayedCrossfade = DjDeck.Approach(displayedCrossfade, crossfade, 6, seconds);
        Canvas.SetLeft(knob, 7 + displayedCrossfade * FaderTravel);

        meterClock += seconds;
        if (meterClock < 0.07)
        {
            return;
        }

        meterClock = 0;
        for (var channel = 0; channel < 2; channel++)
        {
            var target = Live ? 0.35 + random.NextDouble() * 0.65 : 0;
            levels[channel] = target > levels[channel] ? target : levels[channel] * 0.72;
            var lit = (int)Math.Round(levels[channel] * Segments);
            for (var segment = 0; segment < Segments; segment++)
            {
                meters[channel, segment].Fill = segment >= lit ? Unlit
                    : segment >= Segments - 1 ? SetupTheme.Failure
                    : segment >= Segments - 3 ? SetupTheme.Warning
                    : SetupTheme.Playhead;
            }
        }
    }
}
