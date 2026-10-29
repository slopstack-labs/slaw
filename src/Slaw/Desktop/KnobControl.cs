using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// A rotary knob in the Ableton device-panel tradition: a 270 degree arc, a
/// label above, a value below, and a drag gesture that every musician already
/// knows and every accountant finds mildly alarming. Drawn directly, because a
/// control template for a circle is a meeting waiting to happen.
/// </summary>
internal sealed class KnobControl : Control
{
    private const double ControlWidth = 64;
    private const double ControlHeight = 78;
    private const double ArcCenterY = 41;
    private const double ArcRadius = 21;
    private const double ArcThickness = 4;
    private const double ArcStartDegrees = 135;
    private const double ArcSweepDegrees = 270;
    private const double DragPixelsForFullRange = 200;
    private const double FineDragDivisor = 10;
    private const double WheelNormalizedStepPerNotch = 0.01;
    private const long WheelGestureIdleMilliseconds = 500;
    private const double CaptionFontSize = 10;

    private static readonly IBrush TrackBrush = StudioTheme.CellOdd;
    private static readonly IBrush HoveredTrackBrush = new SolidColorBrush(Color.Parse("#34344a"));
    private static readonly IPen PointerPen = new Pen(StudioTheme.Text, 2, lineCap: PenLineCap.Round);

    private readonly string label;
    private readonly double minimum;
    private readonly double maximum;
    private readonly double defaultValue;
    private readonly bool logarithmic;
    private readonly bool integer;
    private readonly Func<double, string> formatValue;

    private double currentValue;
    private bool hovered;
    private bool aiHighlighted;
    private bool dragging;
    private Point lastDragPosition;
    private double dragNormalizedPosition;
    private long lastWheelTimestampMilliseconds = long.MinValue;

    public KnobControl(
        string label,
        double minimum,
        double maximum,
        double defaultValue,
        double initialValue,
        bool logarithmic,
        bool integer,
        Func<double, string> formatValue)
    {
        this.label = label;
        this.minimum = minimum;
        this.maximum = maximum;
        this.integer = integer;
        this.logarithmic = logarithmic && minimum > 0 && maximum > minimum;
        this.formatValue = formatValue;
        this.defaultValue = Coerce(defaultValue);
        currentValue = Coerce(initialValue);
        Width = ControlWidth;
        Height = ControlHeight;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        RefreshToolTip();
    }

    /// <summary>Raised on pointer press, and before the first wheel change of a gesture, so the host can snapshot for undo.</summary>
    public event Action? EditStarted;

    /// <summary>Raised when the user changes the value. Programmatic assignment stays silent.</summary>
    public event Action<double>? ValueChanged;

    /// <summary>Set by the host when the AI changed this parameter recently; the value arc turns violet.</summary>
    public bool AiHighlighted
    {
        get => aiHighlighted;
        set
        {
            if (aiHighlighted != value)
            {
                aiHighlighted = value;
                InvalidateVisual();
            }
        }
    }

    public double Value
    {
        get => currentValue;
        set
        {
            currentValue = Coerce(value);
            RefreshToolTip();
            InvalidateVisual();
        }
    }

    private double Coerce(double candidateValue)
    {
        var clampedValue = Math.Clamp(candidateValue, minimum, maximum);
        return integer ? Math.Round(clampedValue) : clampedValue;
    }

    private double ToNormalized(double parameterValue) =>
        maximum <= minimum
            ? 0
            : logarithmic
                ? Math.Log(parameterValue / minimum) / Math.Log(maximum / minimum)
                : (parameterValue - minimum) / (maximum - minimum);

    private double FromNormalized(double normalizedPosition)
    {
        var clampedPosition = Math.Clamp(normalizedPosition, 0, 1);
        var parameterValue = logarithmic
            ? minimum * Math.Pow(maximum / minimum, clampedPosition)
            : minimum + clampedPosition * (maximum - minimum);
        return Coerce(parameterValue);
    }

    private void RefreshToolTip() =>
        ToolTip.SetTip(this, $"{label}: {formatValue(currentValue)}\ndrag · wheel · double-click resets");

    private void ApplyUserValue(double newValue)
    {
        var coercedValue = Coerce(newValue);
        if (coercedValue.Equals(currentValue))
        {
            return;
        }
        currentValue = coercedValue;
        RefreshToolTip();
        InvalidateVisual();
        ValueChanged?.Invoke(currentValue);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext drawingContext)
    {
        // A transparent fill makes the empty corners hit-testable.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ControlWidth, ControlHeight));

        var arcCenter = new Point(ControlWidth / 2, ArcCenterY);
        var trackPen = new Pen(hovered ? HoveredTrackBrush : TrackBrush, ArcThickness, lineCap: PenLineCap.Round);
        drawingContext.DrawGeometry(null, trackPen, BuildArc(arcCenter, ArcStartDegrees, ArcSweepDegrees));

        var normalizedPosition = Math.Clamp(ToNormalized(currentValue), 0, 1);
        var valueSweepDegrees = normalizedPosition * ArcSweepDegrees;
        if (valueSweepDegrees > 0.5)
        {
            var valuePen = new Pen(aiHighlighted ? StudioTheme.Violet : StudioTheme.Accent, ArcThickness, lineCap: PenLineCap.Round);
            drawingContext.DrawGeometry(null, valuePen, BuildArc(arcCenter, ArcStartDegrees, valueSweepDegrees));
        }

        var pointerAngleRadians = (ArcStartDegrees + valueSweepDegrees) * Math.PI / 180;
        var pointerDirection = new Vector(Math.Cos(pointerAngleRadians), Math.Sin(pointerAngleRadians));
        drawingContext.DrawLine(
            PointerPen,
            arcCenter + pointerDirection * (ArcRadius - 11),
            arcCenter + pointerDirection * (ArcRadius - 5));

        DrawCenteredCaption(drawingContext, label, 1, StudioTheme.TextMuted);
        DrawCenteredCaption(drawingContext, formatValue(currentValue), ControlHeight - 14, StudioTheme.Text);
    }

    private static StreamGeometry BuildArc(Point arcCenter, double startDegrees, double sweepDegrees)
    {
        var startRadians = startDegrees * Math.PI / 180;
        var endRadians = (startDegrees + sweepDegrees) * Math.PI / 180;
        var arcGeometry = new StreamGeometry();
        using (var geometryContext = arcGeometry.Open())
        {
            geometryContext.BeginFigure(
                new Point(arcCenter.X + ArcRadius * Math.Cos(startRadians), arcCenter.Y + ArcRadius * Math.Sin(startRadians)),
                false);
            geometryContext.ArcTo(
                new Point(arcCenter.X + ArcRadius * Math.Cos(endRadians), arcCenter.Y + ArcRadius * Math.Sin(endRadians)),
                new Size(ArcRadius, ArcRadius),
                0,
                sweepDegrees > 180,
                SweepDirection.Clockwise);
            geometryContext.EndFigure(false);
        }
        return arcGeometry;
    }

    private static void DrawCenteredCaption(DrawingContext drawingContext, string captionText, double captionTop, IBrush captionBrush)
    {
        var formattedCaption = new FormattedText(
            captionText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, CaptionFontSize, captionBrush)
        {
            MaxTextWidth = ControlWidth - 2,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        drawingContext.DrawText(formattedCaption, new Point((ControlWidth - formattedCaption.Width) / 2, captionTop));
    }

    // ── Pointer interaction ───────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs pointerEvent)
    {
        base.OnPointerPressed(pointerEvent);
        if (!pointerEvent.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        EditStarted?.Invoke();
        pointerEvent.Handled = true;
        if (pointerEvent.ClickCount >= 2)
        {
            ApplyUserValue(defaultValue);
            return;
        }

        dragging = true;
        lastDragPosition = pointerEvent.GetPosition(this);
        dragNormalizedPosition = ToNormalized(currentValue);
        pointerEvent.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs pointerEvent)
    {
        base.OnPointerMoved(pointerEvent);
        if (!dragging)
        {
            return;
        }
        var pointerPosition = pointerEvent.GetPosition(this);
        var verticalTravel = lastDragPosition.Y - pointerPosition.Y;
        lastDragPosition = pointerPosition;

        var fineDivisor = pointerEvent.KeyModifiers.HasFlag(KeyModifiers.Shift) ? FineDragDivisor : 1;
        dragNormalizedPosition = Math.Clamp(dragNormalizedPosition + verticalTravel / DragPixelsForFullRange / fineDivisor, 0, 1);
        ApplyUserValue(FromNormalized(dragNormalizedPosition));
        pointerEvent.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs pointerEvent)
    {
        base.OnPointerReleased(pointerEvent);
        if (dragging)
        {
            dragging = false;
            pointerEvent.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs captureLostEvent)
    {
        base.OnPointerCaptureLost(captureLostEvent);
        dragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs wheelEvent)
    {
        base.OnPointerWheelChanged(wheelEvent);
        var wheelDelta = wheelEvent.Delta.Y != 0 ? wheelEvent.Delta.Y : wheelEvent.Delta.X;
        if (wheelDelta == 0)
        {
            return;
        }

        var nowMilliseconds = Environment.TickCount64;
        if (nowMilliseconds - lastWheelTimestampMilliseconds > WheelGestureIdleMilliseconds)
        {
            EditStarted?.Invoke();
        }
        lastWheelTimestampMilliseconds = nowMilliseconds;

        ApplyUserValue(integer
            ? currentValue + Math.Sign(wheelDelta)
            : FromNormalized(ToNormalized(currentValue) + wheelDelta * WheelNormalizedStepPerNotch));
        wheelEvent.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs pointerEvent)
    {
        base.OnPointerEntered(pointerEvent);
        hovered = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs pointerEvent)
    {
        base.OnPointerExited(pointerEvent);
        hovered = false;
        InvalidateVisual();
    }
}
