using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>What is being dragged.</summary>
internal enum DragPayloadKind
{
    Instrument,
    Effect,
    Sample,
    Clip,
    Scene,
    EffectDevice,
    Files,
}

/// <summary>
/// A drag in flight: something from the browser, the session grid, the
/// arrangement, the device chain — or files from the operating system.
/// </summary>
internal sealed record DragPayload(
    DragPayloadKind Kind,
    string Value = "",
    int TrackIndex = -1,
    int SceneIndex = -1,
    int Index = -1,
    IReadOnlyList<string>? FilePaths = null)
{
    public string Serialize() => string.Join('\u001f',
        Kind.ToString(), Value,
        TrackIndex.ToString(CultureInfo.InvariantCulture),
        SceneIndex.ToString(CultureInfo.InvariantCulture),
        Index.ToString(CultureInfo.InvariantCulture));

    public static DragPayload? Deserialize(string serializedPayload)
    {
        var payloadFields = serializedPayload.Split('\u001f');
        if (payloadFields.Length != 5 || !Enum.TryParse<DragPayloadKind>(payloadFields[0], out var payloadKind))
        {
            return null;
        }
        return new DragPayload(payloadKind, payloadFields[1],
            int.Parse(payloadFields[2], CultureInfo.InvariantCulture),
            int.Parse(payloadFields[3], CultureInfo.InvariantCulture),
            int.Parse(payloadFields[4], CultureInfo.InvariantCulture));
    }
}

/// <summary>Where something is being dropped.</summary>
internal enum DropTargetKind
{
    /// <summary>A session clip slot or an arrangement lane: a track at a scene.</summary>
    ClipSlot,

    /// <summary>A track header or mixer strip.</summary>
    Track,

    /// <summary>Empty space in the session or arrangement view.</summary>
    EmptyArea,

    /// <summary>The selected track's device chain.</summary>
    DeviceChain,

    /// <summary>An effect device inside the chain (drop before it).</summary>
    EffectDevice,

    /// <summary>The sample editor of a sampler track.</summary>
    SampleEditor,


    /// <summary>The end of the arrangement, or the Arrangement view toggle.</summary>
    ArrangementEnd,
}

internal readonly record struct DropTarget(DropTargetKind Kind, int TrackIndex = -1, int SceneIndex = -1, int Index = -1);

/// <summary>
/// Turns controls into drag sources and drop targets. Sources only start a
/// drag after the pointer moves a few pixels, so clicks and double-clicks keep
/// working; targets light up while something they accept hovers over them.
/// </summary>
internal static class StudioDragDrop
{
    public const string DropHoverClass = "slaw-drop-hover";
    private const double DragStartDistance = 6;

    private static readonly DataFormat<string> SlawPayloadFormat = DataFormat.CreateStringApplicationFormat("slaw-drag-payload");

    /// <summary>
    /// Makes <paramref name="dragSource"/> draggable. <paramref name="payloadFactory"/>
    /// is asked for the payload when a drag starts; returning null cancels it.
    /// </summary>
    public static void MakeDragSource(Control dragSource, Func<DragPayload?> payloadFactory)
    {
        Point? pressedPosition = null;
        var dragInProgress = false;

        dragSource.AddHandler(InputElement.PointerPressedEvent, (_, pointerEvent) =>
        {
            pressedPosition = pointerEvent.GetCurrentPoint(dragSource).Properties.IsLeftButtonPressed
                ? pointerEvent.GetPosition(dragSource)
                : null;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        dragSource.AddHandler(InputElement.PointerReleasedEvent, (_, _) => pressedPosition = null,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        dragSource.AddHandler(InputElement.PointerMovedEvent, async (_, pointerEvent) =>
        {
            if (dragInProgress || pressedPosition is not { } dragOrigin ||
                !pointerEvent.GetCurrentPoint(dragSource).Properties.IsLeftButtonPressed)
            {
                return;
            }
            var pointerOffset = pointerEvent.GetPosition(dragSource) - dragOrigin;
            if (Math.Abs(pointerOffset.X) + Math.Abs(pointerOffset.Y) < DragStartDistance)
            {
                return;
            }
            pressedPosition = null;
            if (payloadFactory() is not { } dragPayload)
            {
                return;
            }
            dragInProgress = true;
            try
            {
                var dragData = new DataTransfer();
                dragData.Add(DataTransferItem.Create(SlawPayloadFormat, dragPayload.Serialize()));
                await DragDrop.DoDragDropAsync(pointerEvent, dragData, DragDropEffects.Copy | DragDropEffects.Move);
            }
            finally
            {
                dragInProgress = false;
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Makes <paramref name="dropTarget"/> accept drops. The target is resolved
    /// lazily so it reflects the project at drop time, not at construction time.
    /// </summary>
    public static void MakeDropTarget(Control dropTarget, IStudioHost host, Func<DropTarget> targetFactory)
    {
        DragDrop.SetAllowDrop(dropTarget, true);

        // Local values beat styles, so the highlight swaps properties directly
        // and puts the originals back afterwards.
        IBrush? originalBorderBrush = null;
        Thickness originalBorderThickness = default;
        IBrush? originalBackground = null;
        var hoverApplied = false;

        void SetHover(bool hovered)
        {
            if (hovered == hoverApplied)
            {
                return;
            }
            hoverApplied = hovered;
            dropTarget.Classes.Set(DropHoverClass, hovered);
            switch (dropTarget)
            {
                case Border hoveredBorder when hovered:
                    originalBorderBrush = hoveredBorder.BorderBrush;
                    originalBorderThickness = hoveredBorder.BorderThickness;
                    hoveredBorder.BorderBrush = StudioTheme.Playhead;
                    hoveredBorder.BorderThickness = new Thickness(2);
                    break;
                case Border hoveredBorder:
                    hoveredBorder.BorderBrush = originalBorderBrush;
                    hoveredBorder.BorderThickness = originalBorderThickness;
                    break;
                case Avalonia.Controls.Primitives.ToggleButton or Button when hovered:
                    originalBorderBrush = ((Avalonia.Controls.Primitives.TemplatedControl)dropTarget).BorderBrush;
                    ((Avalonia.Controls.Primitives.TemplatedControl)dropTarget).BorderBrush = StudioTheme.Playhead;
                    break;
                case Avalonia.Controls.Primitives.ToggleButton or Button:
                    ((Avalonia.Controls.Primitives.TemplatedControl)dropTarget).BorderBrush = originalBorderBrush;
                    break;
                case Avalonia.Controls.Primitives.TemplatedControl hoveredArea when hovered:
                    originalBackground = hoveredArea.Background;
                    hoveredArea.Background = DropAreaTint;
                    break;
                case Avalonia.Controls.Primitives.TemplatedControl hoveredArea:
                    hoveredArea.Background = originalBackground;
                    break;
            }
            dropTarget.InvalidateVisual();
        }

        dropTarget.AddHandler(DragDrop.DragOverEvent, (_, dragEvent) =>
        {
            var dragPayload = ReadPayload(dragEvent);
            var accepted = dragPayload is not null && host.CanDrop(dragPayload, targetFactory());
            dragEvent.DragEffects = accepted
                ? IsCopyGesture(dragEvent) || dragPayload!.Kind != DragPayloadKind.Clip ? DragDropEffects.Copy : DragDropEffects.Move
                : DragDropEffects.None;
            SetHover(accepted);
            // Handled either way, so an outer target does not light up as well.
            dragEvent.Handled = accepted;
        });
        dropTarget.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetHover(false));
        dropTarget.AddHandler(DragDrop.DropEvent, (_, dragEvent) =>
        {
            SetHover(false);
            if (ReadPayload(dragEvent) is not { } dragPayload)
            {
                return;
            }
            var resolvedTarget = targetFactory();
            if (!host.CanDrop(dragPayload, resolvedTarget))
            {
                return;
            }
            host.Drop(dragPayload, resolvedTarget, IsCopyGesture(dragEvent));
            dragEvent.Handled = true;
        });
    }

    private static bool IsCopyGesture(DragEventArgs dragEvent) =>
        dragEvent.KeyModifiers.HasFlag(KeyModifiers.Control) || dragEvent.KeyModifiers.HasFlag(KeyModifiers.Alt);

    /// <summary>Reads an internal payload, or wraps dropped files as one.</summary>
    public static DragPayload? ReadPayload(DragEventArgs dragEvent)
    {
        if (dragEvent.DataTransfer.TryGetValue(SlawPayloadFormat) is { } serializedPayload)
        {
            return DragPayload.Deserialize(serializedPayload);
        }
        if (dragEvent.DataTransfer.Contains(DataFormat.File))
        {
            var droppedPaths = dragEvent.DataTransfer.TryGetFiles()?
                .Select(droppedItem => droppedItem.TryGetLocalPath())
                .OfType<string>()
                .ToList();
            return droppedPaths is { Count: > 0 } ? new DragPayload(DragPayloadKind.Files, FilePaths: droppedPaths) : null;
        }
        return null;
    }

    /// <summary>Whether a control is currently highlighted as a drop target (for custom-drawn controls).</summary>
    public static bool IsDropHovered(Control control) => control.Classes.Contains(DropHoverClass);

    private static readonly IBrush DropAreaTint = new SolidColorBrush(Color.Parse("#22c55e"), 0.08);

    /// <summary>A small "⠿" handle for things that contain their own interactive controls (knobs, text boxes).</summary>
    public static TextBlock DragGrip(string toolTip)
    {
        var dragGrip = new TextBlock
        {
            Text = "⠿",
            FontSize = 14,
            Foreground = StudioTheme.TextMuted,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Padding = new Thickness(2, 0),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        ToolTip.SetTip(dragGrip, toolTip);
        return dragGrip;
    }

    /// <summary>The outline custom-drawn controls draw while hovered by an acceptable drag.</summary>
    public static readonly IPen DropHoverPen = new Pen(StudioTheme.Playhead, 2, new DashStyle(new double[] { 4, 2 }, 0));
}
