using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// One clip envelope, drawn under the clip editor with the same step columns:
/// click to add a breakpoint, drag to move it, right-click or double-click to
/// remove it. Points snap to quarter steps (hold Shift for none). Edits happen
/// in place on the clip; the host snapshots for undo around each gesture.
/// </summary>
internal sealed class AutomationLaneControl : Control
{
    private const double PointRadius = 4.5;
    private const double HitRadius = 8;
    private const double VerticalPadding = 6;
    private const double SnapResolutionSteps = 0.25;

    private static readonly Typeface ValueTypeface = new(StudioTheme.MonospaceFont, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IPen StepLinePen = new Pen(new SolidColorBrush(Color.Parse("#2a2a3a"), 0.6), 1);
    private static readonly IPen BeatLinePen = new Pen(new SolidColorBrush(Color.Parse("#4a4a5e"), 0.9), 1);
    private static readonly IPen BaseValuePen = new Pen(StudioTheme.TextDim, 1, new DashStyle(new double[] { 3, 3 }, 0));
    private static readonly IPen PlayheadPen = new Pen(StudioTheme.Playhead, 1.5);

    private readonly SlawClip clip;
    private readonly AutomationTarget target;
    private readonly double baseValue;
    private readonly double leadingGutterWidth;
    private readonly bool groupedDrumLayout;
    private readonly IPen envelopePen;
    private readonly IBrush envelopeFill;
    private readonly IBrush pointBrush;

    private AutomationPoint? draggedPoint;
    private AutomationPoint? hoveredPoint;
    private bool gestureChangedEnvelope;
    private double? playheadStep;

    /// <param name="baseValue">The knob's value, drawn while the lane is empty.</param>
    /// <param name="leadingGutterWidth">Left inset so columns line up with the editor above (the piano roll's keyboard).</param>
    /// <param name="groupedDrumLayout">Match the drum step row's grouped cells instead of uniform columns.</param>
    public AutomationLaneControl(SlawClip clip, AutomationTarget target, double baseValue, double leadingGutterWidth, bool groupedDrumLayout, IBrush laneBrush)
    {
        this.clip = clip;
        this.target = target;
        this.baseValue = baseValue;
        this.leadingGutterWidth = leadingGutterWidth;
        this.groupedDrumLayout = groupedDrumLayout;
        envelopePen = new Pen(laneBrush, 2);
        pointBrush = laneBrush;
        envelopeFill = laneBrush is ISolidColorBrush solidLaneBrush ? new SolidColorBrush(solidLaneBrush.Color, 0.14) : StudioTheme.AccentSoft;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        ToolTip.SetTip(this, "click: add point · drag: move (Shift: no snap) · right-click / double-click: remove");
    }

    /// <summary>Raised before a gesture mutates the envelope, so the host can snapshot for undo.</summary>
    public event Action? EditStarted;

    /// <summary>Raised after a gesture changed the envelope, with a description for the co-pilot.</summary>
    public event Action<string>? EnvelopeEdited;

    public int? PlayheadStep
    {
        set
        {
            if (playheadStep != value)
            {
                playheadStep = value;
                InvalidateVisual();
            }
        }
    }

    private List<AutomationPoint>? Points => clip.Automation?.GetValueOrDefault(target.Key);

    // ── Geometry ──────────────────────────────────────────────────────────────

    /// <summary>
    /// How many steps the envelope spans when that is not the pattern's length: an arrangement
    /// clip's envelopes cover the whole clip while its pattern loops underneath. Null: the pattern.
    /// </summary>
    public int? EnvelopeLengthSteps { get; init; }

    private int StepCount => Math.Max(1, EnvelopeLengthSteps ?? clip.Pattern.Count);

    private double GridWidth => Math.Max(1, Bounds.Width - leadingGutterWidth);

    // Mirrors StepRowControl: 4 px between cells, 8 more between beats.
    private double DrumCellWidth => Math.Max(8, (GridWidth - 4 * (StepCount - 1) - 8 * ((StepCount - 1) / 4)) / StepCount);

    private double StepToX(double step)
    {
        if (!groupedDrumLayout)
        {
            return leadingGutterWidth + step / StepCount * GridWidth;
        }
        var wholeStep = Math.Clamp((int)Math.Floor(step), 0, StepCount);
        var cellLeft = wholeStep * (DrumCellWidth + 4) + wholeStep / 4 * 8 - (wholeStep == StepCount ? 4 : 0);
        return leadingGutterWidth + cellLeft + (step - wholeStep) * (DrumCellWidth + 4);
    }

    private double XToStep(double pointerX)
    {
        if (!groupedDrumLayout)
        {
            return Math.Clamp((pointerX - leadingGutterWidth) / GridWidth * StepCount, 0, StepCount);
        }
        for (var stepIndex = 0; stepIndex < StepCount; stepIndex++)
        {
            if (pointerX < StepToX(stepIndex + 1))
            {
                return Math.Clamp(stepIndex + (pointerX - StepToX(stepIndex)) / (DrumCellWidth + 4), 0, StepCount);
            }
        }
        return StepCount;
    }

    private double Normalize(double parameterValue)
    {
        var specification = target.Specification;
        if (specification.Logarithmic && specification.Minimum > 0)
        {
            return Math.Log(Math.Max(parameterValue, specification.Minimum) / specification.Minimum) / Math.Log(specification.Maximum / specification.Minimum);
        }
        return (parameterValue - specification.Minimum) / Math.Max(1e-9, specification.Maximum - specification.Minimum);
    }

    private double Denormalize(double normalizedValue)
    {
        var specification = target.Specification;
        normalizedValue = Math.Clamp(normalizedValue, 0, 1);
        var parameterValue = specification.Logarithmic && specification.Minimum > 0
            ? specification.Minimum * Math.Pow(specification.Maximum / specification.Minimum, normalizedValue)
            : specification.Minimum + normalizedValue * (specification.Maximum - specification.Minimum);
        return specification.Clamp(parameterValue);
    }

    private double ValueToY(double parameterValue) => VerticalPadding + (1 - Math.Clamp(Normalize(parameterValue), 0, 1)) * (Bounds.Height - 2 * VerticalPadding);

    private double YToValue(double pointerY) => Denormalize(1 - (pointerY - VerticalPadding) / Math.Max(1, Bounds.Height - 2 * VerticalPadding));

    private AutomationPoint? PointAt(Point pointerPosition) => Points?
        .Select(point => (Point: point, Distance: Math.Abs(StepToX(point.Step) - pointerPosition.X) + Math.Abs(ValueToY(point.Value) - pointerPosition.Y)))
        .Where(candidate => candidate.Distance <= HitRadius * 1.5)
        .OrderBy(candidate => candidate.Distance)
        .Select(candidate => candidate.Point)
        .FirstOrDefault();

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(StudioTheme.CellEven, null, new Rect(leadingGutterWidth, 0, GridWidth, Bounds.Height));
        for (var stepIndex = 0; stepIndex <= StepCount; stepIndex++)
        {
            var stepX = Math.Round(StepToX(stepIndex)) + 0.5;
            drawingContext.DrawLine(stepIndex % 4 == 0 ? BeatLinePen : StepLinePen, new Point(stepX, 0), new Point(stepX, Bounds.Height));
        }

        var points = Points;
        if (points is not { Count: > 0 })
        {
            var baseY = ValueToY(baseValue);
            drawingContext.DrawLine(BaseValuePen, new Point(leadingGutterWidth, baseY), new Point(Bounds.Width, baseY));
            DrawLabel(drawingContext, $"{target.Label}: {target.Specification.Format(baseValue)} (knob) — click to automate", new Point(leadingGutterWidth + 6, 4), StudioTheme.TextMuted);
        }
        else
        {
            // Values hold before the first and after the last point, so the curve spans the whole clip.
            var curvePoints = new List<Point> { new(StepToX(0), ValueToY(points[0].Value)) };
            curvePoints.AddRange(points.Select(point => new Point(StepToX(point.Step), ValueToY(point.Value))));
            curvePoints.Add(new Point(StepToX(StepCount), ValueToY(points[^1].Value)));

            var fillGeometry = new StreamGeometry();
            using (var fillContext = fillGeometry.Open())
            {
                fillContext.BeginFigure(new Point(curvePoints[0].X, Bounds.Height), true);
                foreach (var curvePoint in curvePoints)
                {
                    fillContext.LineTo(curvePoint);
                }
                fillContext.LineTo(new Point(curvePoints[^1].X, Bounds.Height));
                fillContext.EndFigure(true);
            }
            drawingContext.DrawGeometry(envelopeFill, null, fillGeometry);
            for (var segmentIndex = 1; segmentIndex < curvePoints.Count; segmentIndex++)
            {
                drawingContext.DrawLine(envelopePen, curvePoints[segmentIndex - 1], curvePoints[segmentIndex]);
            }
            foreach (var point in points)
            {
                var emphasized = ReferenceEquals(point, hoveredPoint) || ReferenceEquals(point, draggedPoint);
                drawingContext.DrawEllipse(emphasized ? StudioTheme.Text : pointBrush, null,
                    new Point(StepToX(point.Step), ValueToY(point.Value)), PointRadius, PointRadius);
            }
            DrawLabel(drawingContext, target.Label, new Point(leadingGutterWidth + 6, 4), StudioTheme.TextMuted);
        }

        if ((draggedPoint ?? hoveredPoint) is { } focusedPoint)
        {
            var readout = string.Create(CultureInfo.InvariantCulture, $"step {focusedPoint.Step + 1:0.##} · {target.Specification.Format(focusedPoint.Value)}");
            DrawLabel(drawingContext, readout, new Point(Math.Min(StepToX(focusedPoint.Step) + 8, Bounds.Width - 150), Math.Max(4, ValueToY(focusedPoint.Value) - 18)), StudioTheme.Text);
        }

        if (playheadStep is { } currentStep)
        {
            var playheadX = StepToX(currentStep + 0.5);
            drawingContext.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, Bounds.Height));
        }
    }

    private static void DrawLabel(DrawingContext drawingContext, string labelText, Point origin, IBrush foreground) =>
        drawingContext.DrawText(new FormattedText(labelText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ValueTypeface, 10, foreground), origin);

    // ── Input ─────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs pointerEvent)
    {
        base.OnPointerPressed(pointerEvent);
        var pointerPosition = pointerEvent.GetPosition(this);
        if (pointerPosition.X < leadingGutterWidth)
        {
            return;
        }
        var pointerProperties = pointerEvent.GetCurrentPoint(this).Properties;
        var pressedPoint = PointAt(pointerPosition);
        var removalRequested = pointerProperties.IsRightButtonPressed || (pointerProperties.IsLeftButtonPressed && pointerEvent.ClickCount >= 2);

        if (removalRequested)
        {
            if (pressedPoint is not null)
            {
                EditStarted?.Invoke();
                RemovePoint(pressedPoint);
                EnvelopeEdited?.Invoke($"removed a {target.Label} breakpoint");
            }
            pointerEvent.Handled = true;
            return;
        }
        if (!pointerProperties.IsLeftButtonPressed)
        {
            return;
        }

        EditStarted?.Invoke();
        gestureChangedEnvelope = false;
        if (pressedPoint is null)
        {
            pressedPoint = new AutomationPoint { Step = SnappedStep(pointerPosition.X, pointerEvent.KeyModifiers), Value = YToValue(pointerPosition.Y) };
            var envelopes = clip.Automation ??= new Dictionary<string, List<AutomationPoint>>();
            if (!envelopes.TryGetValue(target.Key, out var lanePoints))
            {
                lanePoints = new List<AutomationPoint>();
                envelopes[target.Key] = lanePoints;
            }
            lanePoints.Add(pressedPoint);
            SortPoints();
            gestureChangedEnvelope = true;
        }
        draggedPoint = pressedPoint;
        pointerEvent.Pointer.Capture(this);
        pointerEvent.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs pointerEvent)
    {
        base.OnPointerMoved(pointerEvent);
        var pointerPosition = pointerEvent.GetPosition(this);
        if (draggedPoint is not null)
        {
            draggedPoint.Step = SnappedStep(pointerPosition.X, pointerEvent.KeyModifiers);
            draggedPoint.Value = YToValue(pointerPosition.Y);
            SortPoints();
            gestureChangedEnvelope = true;
            InvalidateVisual();
            return;
        }
        var nowHovered = PointAt(pointerPosition);
        if (!ReferenceEquals(nowHovered, hoveredPoint))
        {
            hoveredPoint = nowHovered;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs pointerEvent)
    {
        base.OnPointerReleased(pointerEvent);
        if (draggedPoint is null)
        {
            return;
        }
        draggedPoint = null;
        pointerEvent.Pointer.Capture(null);
        if (gestureChangedEnvelope)
        {
            EnvelopeEdited?.Invoke($"automated {target.Label}");
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs pointerEvent)
    {
        base.OnPointerExited(pointerEvent);
        if (hoveredPoint is not null)
        {
            hoveredPoint = null;
            InvalidateVisual();
        }
    }

    private double SnappedStep(double pointerX, KeyModifiers keyModifiers)
    {
        var rawStep = XToStep(pointerX);
        return keyModifiers.HasFlag(KeyModifiers.Shift) ? rawStep : Math.Round(rawStep / SnapResolutionSteps) * SnapResolutionSteps;
    }

    private void SortPoints()
    {
        // Stable, so a dragged point passing a neighbour swaps with it instead of jittering.
        if (Points is { } lanePoints)
        {
            var sortedPoints = lanePoints.OrderBy(point => point.Step).ToList();
            lanePoints.Clear();
            lanePoints.AddRange(sortedPoints);
        }
    }

    private void RemovePoint(AutomationPoint removedPoint)
    {
        if (clip.Automation is not { } envelopes || !envelopes.TryGetValue(target.Key, out var lanePoints))
        {
            return;
        }
        lanePoints.Remove(removedPoint);
        if (lanePoints.Count == 0)
        {
            envelopes.Remove(target.Key);
        }
        if (envelopes.Count == 0)
        {
            clip.Automation = null;
        }
        hoveredPoint = null;
        InvalidateVisual();
    }
}
