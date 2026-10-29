using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// What the views may ask of the studio: transport control and AI assistance.
/// Views never talk to the engine or the inference substrate directly.
/// </summary>
internal interface IStudioHost
{
    /// <summary>The user wants AI features visible at all (the master switch).</summary>
    bool AiFeaturesEnabled { get; }

    void LaunchClip(int trackIndex, int sceneIndex);
    void LaunchScene(int sceneIndex);
    void StopTrack(int trackIndex);
    void StopAll();
    void PlayArrangementFrom(int sectionIndex);

    void GenerateClipWithAi(int trackIndex, int sceneIndex);
    void RenameTrackWithAi(int trackIndex);

    /// <summary>Lets the model set a device. <paramref name="effectIndex"/> -1 is the instrument, -2 the master bus.</summary>
    void TuneDeviceWithAi(int trackIndex, int effectIndex);

    bool IsAiTuned(string parameterAddress);
    void ForgetAiTuning(string parameterAddress);
}

/// <summary>Small factories for the controls every panel needs, styled consistently.</summary>
internal static class StudioUi
{
    public static Button Button(string caption, Action onClick, string? toolTip = null, bool compact = false)
    {
        var createdButton = new Button
        {
            Content = caption,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = compact ? new Thickness(7, 3) : new Thickness(12, 5),
            FontSize = compact ? 12 : 13,
        };
        createdButton.Click += (_, _) => onClick();
        if (toolTip is not null)
        {
            ToolTip.SetTip(createdButton, toolTip);
        }
        return createdButton;
    }

    public static Button AccentButton(string caption, Action onClick, string? toolTip = null, bool compact = false)
    {
        var accentButton = Button(caption, onClick, toolTip, compact);
        accentButton.Classes.Add("accent");
        accentButton.FontWeight = FontWeight.SemiBold;
        return accentButton;
    }

    /// <summary>A button for something the AI does. Only ever built while AI features are enabled.</summary>
    public static Button AiButton(string caption, Action onClick, string toolTip, bool compact = true)
    {
        var aiButton = Button(caption, onClick, toolTip, compact);
        aiButton.Background = StudioTheme.AiGradient;
        aiButton.Foreground = Brushes.White;
        return aiButton;
    }

    public static TextBlock Caption(string text, double fontSize = 11, IBrush? foreground = null, FontWeight fontWeight = FontWeight.Normal) => new()
    {
        Text = text,
        FontSize = fontSize,
        Foreground = foreground ?? StudioTheme.TextMuted,
        FontWeight = fontWeight,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock SectionHeading(string text) =>
        Caption(text.ToUpperInvariant(), 10, StudioTheme.TextMuted, FontWeight.Bold);

    public static Border Panel(Control content, Thickness? padding = null, IBrush? background = null) => new()
    {
        Background = background ?? StudioTheme.Surface,
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(10),
        Child = content,
    };

    public static Control VerticalSeparator() => new Border
    {
        Width = 1,
        Height = 24,
        Background = StudioTheme.Border,
        Margin = new Thickness(4, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children)
        {
            rowPanel.Children.Add(child);
        }
        return rowPanel;
    }

    public static KnobControl Knob(ParameterSpecification parameterSpecification, double currentValue) =>
        new(parameterSpecification.Label, parameterSpecification.Minimum, parameterSpecification.Maximum,
            parameterSpecification.Default, currentValue, parameterSpecification.Logarithmic, parameterSpecification.Integer,
            parameterSpecification.Format);

    public static string GainToDecibels(double linearGain) =>
        linearGain <= 0.0001 ? "-∞ dB" : FormattableString.Invariant($"{20 * Math.Log10(linearGain):+0.0;-0.0;0.0} dB");
}
