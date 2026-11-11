using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// A small picture of an EQ: its response curve, computed by the same arithmetic the audio
/// uses, over the live spectrum of what it is putting out. It watches the device only while on screen.
/// </summary>
internal sealed class EqDisplayControl : Control
{
    private const double LowestHz = 20;
    private const double HighestHz = 20000;
    private const double CurveRangeDecibels = 20;
    private const double SpectrumFloorDecibels = -84;
    private const double SpectrumCeilingDecibels = -6;
    private const int CurveSampleRate = 44100;
    private const int WindowFrames = 2048;
    private const int SpectrumColumns = 90;
    private const double SpectrumFallPerFrame = 0.84;
    private const double SilentLevel = 1e-9;

    private static readonly double[] GridFrequencies = { 100, 1000, 10000 };
    private static readonly IPen GridPen = new Pen(StudioTheme.Border, 1);
    private static readonly IPen CurvePen = new Pen(StudioTheme.Violet, 1.5);
    private static readonly IBrush PlotBrush = StudioTheme.Background;
    private static readonly IBrush SpectrumBrush = StudioTheme.Tint(StudioThemeSlots.Accent, 0.3);

    private readonly Func<Dictionary<string, double>> parameterTable;
    private readonly string owner;
    private readonly int effectIndex;
    private readonly double[] windowShape = new double[WindowFrames];
    private readonly float[] recentSamples = new float[WindowFrames];
    private readonly Complex[] spectrum = new Complex[WindowFrames];
    private readonly double[] columnPowers = new double[SpectrumColumns];
    private readonly double[] curveDecibels = new double[SpectrumColumns * 2];
    private readonly double[] drawnSettings;
    private readonly double windowEnergy;
    private readonly IReadOnlyList<ParameterSpecification> specifications = DeviceCatalog.EffectParametersFor("eq");

    private DispatcherTimer? refreshTimer;
    private EqSpectrumTap? tap;
    private long lastFramesRead;

    public EqDisplayControl(Func<Dictionary<string, double>> parameterTable, string owner, int effectIndex)
    {
        this.parameterTable = parameterTable;
        this.owner = owner;
        this.effectIndex = effectIndex;
        drawnSettings = new double[specifications.Count];
        for (var index = 0; index < WindowFrames; index++)
        {
            windowShape[index] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / WindowFrames);
            windowEnergy += windowShape[index] * windowShape[index];
        }
        Height = 60;
        MinWidth = 180;
        ClipToBounds = true;
        RefreshCurve();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs attachment)
    {
        base.OnAttachedToVisualTree(attachment);
        tap = EqSpectrumTaps.Watch(owner, effectIndex);
        lastFramesRead = 0;
        refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Refresh());
        refreshTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs detachment)
    {
        base.OnDetachedFromVisualTree(detachment);
        refreshTimer?.Stop();
        refreshTimer = null;
        if (tap is not null)
        {
            EqSpectrumTaps.Forget(tap);
            tap = null;
        }
    }

    private void Refresh()
    {
        var needsPainting = RefreshCurve();
        needsPainting |= RefreshSpectrum();
        if (needsPainting)
        {
            InvalidateVisual();
        }
    }

    /// <summary>Knobs, undo, agents and the AI all land in the same table, so watching it covers them all.</summary>
    private bool RefreshCurve()
    {
        var parameters = parameterTable();
        var changed = false;
        for (var index = 0; index < specifications.Count; index++)
        {
            var value = DeviceCatalog.Resolve(parameters, specifications[index]);
            changed |= value != drawnSettings[index];
            drawnSettings[index] = value;
        }
        if (!changed && curveDecibels[0] != 0)
        {
            return false;
        }
        for (var point = 0; point < curveDecibels.Length; point++)
        {
            curveDecibels[point] = FrequencyRealignmentEqProcessor.ResponseDecibels(parameters, FrequencyAt((double)point / (curveDecibels.Length - 1)), CurveSampleRate);
        }
        // Zero marks "not computed yet", and an exactly flat first point is drawn the same with or without it.
        curveDecibels[0] = curveDecibels[0] == 0 ? double.Epsilon : curveDecibels[0];
        return changed;
    }

    private bool RefreshSpectrum()
    {
        if (tap is null)
        {
            return false;
        }
        var written = tap.FramesWritten;
        var hasNewAudio = written != lastFramesRead;
        lastFramesRead = written;
        var anyLevel = false;
        for (var column = 0; column < SpectrumColumns; column++)
        {
            columnPowers[column] *= SpectrumFallPerFrame * SpectrumFallPerFrame;
            anyLevel |= columnPowers[column] > SilentLevel;
        }
        if (!hasNewAudio)
        {
            return anyLevel;
        }
        tap.CopyLatest(recentSamples);
        for (var index = 0; index < WindowFrames; index++)
        {
            spectrum[index] = new Complex(recentSamples[index] * windowShape[index], 0);
        }
        MixAnalyzer.TransformInPlace(spectrum);
        var binHz = (double)Engine44100 / WindowFrames;
        for (var column = 0; column < SpectrumColumns; column++)
        {
            var firstBin = Math.Max(1, (int)(FrequencyAt((double)column / SpectrumColumns) / binHz));
            var lastBin = Math.Clamp((int)(FrequencyAt((double)(column + 1) / SpectrumColumns) / binHz), firstBin, WindowFrames / 2 - 1);
            var loudest = 0.0;
            for (var bin = firstBin; bin <= lastBin; bin++)
            {
                loudest = Math.Max(loudest, spectrum[bin].Real * spectrum[bin].Real + spectrum[bin].Imaginary * spectrum[bin].Imaginary);
            }
            columnPowers[column] = Math.Max(columnPowers[column], loudest * 2 / (WindowFrames * windowEnergy));
        }
        return true;
    }

    /// <summary>Close enough for a picture: at 48 kHz the columns sit a tenth of a semitone-width to the left.</summary>
    private const int Engine44100 = 44100;

    private static double FrequencyAt(double position) => LowestHz * Math.Pow(HighestHz / LowestHz, position);

    private static double PositionOf(double frequency) => Math.Log(frequency / LowestHz) / Math.Log(HighestHz / LowestHz);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.DrawRectangle(PlotBrush, null, new Rect(0, 0, width, height), 3, 3);
        foreach (var frequency in GridFrequencies)
        {
            var x = PositionOf(frequency) * width;
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
        }
        context.DrawLine(GridPen, new Point(0, height / 2), new Point(width, height / 2));

        var columnWidth = width / SpectrumColumns;
        for (var column = 0; column < SpectrumColumns; column++)
        {
            if (columnPowers[column] <= SilentLevel)
            {
                continue;
            }
            var level = (MixAnalyzer.PowerToDecibels(columnPowers[column]) - SpectrumFloorDecibels) / (SpectrumCeilingDecibels - SpectrumFloorDecibels);
            var barHeight = Math.Clamp(level, 0, 1) * height;
            context.DrawRectangle(SpectrumBrush, null, new Rect(column * columnWidth, height - barHeight, columnWidth + 0.5, barHeight));
        }

        var curve = new StreamGeometry();
        using (var geometry = curve.Open())
        {
            for (var point = 0; point < curveDecibels.Length; point++)
            {
                var x = width * point / (curveDecibels.Length - 1);
                var y = height / 2 - Math.Clamp(curveDecibels[point] / CurveRangeDecibels, -1, 1) * (height / 2 - 2);
                if (point == 0)
                {
                    geometry.BeginFigure(new Point(x, y), false);
                }
                else
                {
                    geometry.LineTo(new Point(x, y));
                }
            }
            geometry.EndFigure(false);
        }
        context.DrawGeometry(null, CurvePen, curve);
    }
}
