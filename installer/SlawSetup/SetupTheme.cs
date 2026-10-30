using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SlopstackLabs.Slaw.Setup;

/// <summary>
/// The studio palette (see src/Slaw/Desktop/StudioTheme.cs), restated for WPF.
/// </summary>
internal static class SetupTheme
{
    public static readonly Brush Background = Solid("#0b0b10");
    public static readonly Brush Surface = Solid("#13131b");
    public static readonly Brush SurfaceRaised = Solid("#1c1c28");
    public static readonly Brush Border = Solid("#2a2a3a");
    public static readonly Brush Text = Solid("#ededf2");
    public static readonly Brush TextMuted = Solid("#8a8aa3");
    public static readonly Brush TextDim = Solid("#4a4a5e");
    public static readonly Brush Accent = Solid("#d946ef");
    public static readonly Brush Playhead = Solid("#22c55e");
    public static readonly Brush Warning = Solid("#f59e0b");
    public static readonly Brush Failure = Solid("#ef4444");
    public static readonly Brush AccentGradient = Freeze(new LinearGradientBrush(
        (Color)ColorConverter.ConvertFromString("#a21caf"),
        (Color)ColorConverter.ConvertFromString("#6d28d9"),
        new Point(0, 0), new Point(1, 1)));

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI Semibold, Segoe UI");

    public static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Brush Solid(string hex) => Freeze(new SolidColorBrush(Color(hex)));

    public static Brush Solid(string hex, byte alpha)
    {
        var color = Color(hex);
        color.A = alpha;
        return Freeze(new SolidColorBrush(color));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static TextBlock Label(string text, double size, Brush foreground, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground,
        FontFamily = Font,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Link(string text, Action onClick)
    {
        var link = Label(text, 12.5, Accent);
        link.Cursor = Cursors.Hand;
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        link.MouseLeftButtonUp += (_, _) => onClick();
        return link;
    }
}

/// <summary>A rounded button; the primary one wears the accent gradient.</summary>
internal sealed class PillButton : Border
{
    public event Action? Click;

    public PillButton(string text, bool primary)
    {
        Padding = new Thickness(22, 9, 22, 10);
        Margin = new Thickness(10, 0, 0, 0);
        CornerRadius = new CornerRadius(8);
        Cursor = Cursors.Hand;
        Background = primary ? SetupTheme.AccentGradient : SetupTheme.SurfaceRaised;
        BorderBrush = primary ? Brushes.Transparent : SetupTheme.Border;
        BorderThickness = new Thickness(1);
        Child = SetupTheme.Label(text, 13, SetupTheme.Text, FontWeights.SemiBold);

        MouseEnter += (_, _) => Opacity = IsEnabled ? 0.85 : Opacity;
        MouseLeave += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
        MouseLeftButtonUp += (_, _) =>
        {
            if (IsEnabled)
            {
                Click?.Invoke();
            }
        };
    }
}

/// <summary>A checkbox row drawn in the studio style.</summary>
internal sealed class OptionToggle : Grid
{
    private readonly Border box;
    private readonly TextBlock tick;
    private bool isChecked;

    public OptionToggle(string title, string description, bool initiallyChecked)
    {
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        Margin = new Thickness(0, 0, 0, 10);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());

        tick = new TextBlock
        {
            Text = "✓",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = SetupTheme.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };
        box = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 1, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = tick,
        };
        Children.Add(box);

        var text = new StackPanel();
        text.Children.Add(SetupTheme.Label(title, 13.5, SetupTheme.Text, FontWeights.SemiBold));
        text.Children.Add(SetupTheme.Label(description, 12, SetupTheme.TextMuted));
        SetColumn(text, 1);
        Children.Add(text);

        IsChecked = initiallyChecked;
        MouseLeftButtonUp += (_, _) => IsChecked = !IsChecked;
    }

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            isChecked = value;
            box.Background = value ? SetupTheme.AccentGradient : SetupTheme.SurfaceRaised;
            box.BorderBrush = value ? Brushes.Transparent : SetupTheme.Border;
            tick.Visibility = value ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
