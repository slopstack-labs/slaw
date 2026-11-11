using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The metronome, Ableton-style: the Click button beside Rec turns it on (Ctrl+Shift+K),
/// right-click it for volume, count-in and "only while recording". The click is part of
/// the live output only; bounces and renders never hear it. A count-in clicks one or two
/// bars before a recording starts from a stopped transport, and nothing is recorded until
/// the transport actually starts.
/// </summary>
internal sealed partial class StudioWindow
{
    private ToggleButton? metronomeToggle;
    private CheckBox? metronomeFlyoutEnabledCheckBox;
    private CheckBox? metronomeFlyoutOnlyWhileRecordingCheckBox;
    private Slider? metronomeFlyoutVolumeSlider;
    private TextBlock? metronomeFlyoutVolumeReadout;
    private ComboBox? metronomeFlyoutCountInComboBox;
    private bool synchronizingMetronomeControls;

    private bool RecordEngaged => recordToggle?.IsChecked == true;

    private Control ComposeMetronomeControls()
    {
        metronomeToggle = new ToggleButton
        {
            Content = StudioIcons.Content(StudioIcon.Metronome, "Click", 13),
            Padding = new Thickness(10, 4),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(metronomeToggle,
            "Metronome on / off (Ctrl+Shift+K): a click on every beat, accented on each bar's downbeat. Live output only: never in bounces or renders.\n" +
            "Right-click for volume, count-in (None / 1 bar / 2 bars before recording from a stop) and \"only while recording\".");
        metronomeToggle.IsCheckedChanged += (_, _) =>
        {
            if (synchronizingMetronomeControls)
            {
                return;
            }
            SetMetronomeEnabled(metronomeToggle.IsChecked == true);
        };
        metronomeToggle.ContextFlyout = ComposeMetronomeFlyout();
        return metronomeToggle;
    }

    private Flyout ComposeMetronomeFlyout()
    {
        metronomeFlyoutEnabledCheckBox = new CheckBox { Content = "Metronome on" };
        metronomeFlyoutEnabledCheckBox.IsCheckedChanged += (_, _) =>
        {
            if (!synchronizingMetronomeControls)
            {
                SetMetronomeEnabled(metronomeFlyoutEnabledCheckBox.IsChecked == true);
            }
        };
        metronomeFlyoutOnlyWhileRecordingCheckBox = new CheckBox { Content = "Only while recording" };
        ToolTip.SetTip(metronomeFlyoutOnlyWhileRecordingCheckBox, "Click only while Rec is engaged; plain playback stays silent.");
        metronomeFlyoutOnlyWhileRecordingCheckBox.IsCheckedChanged += (_, _) =>
        {
            if (!synchronizingMetronomeControls)
            {
                SetMetronomeOnlyWhileRecording(metronomeFlyoutOnlyWhileRecordingCheckBox.IsChecked == true);
            }
        };
        metronomeFlyoutVolumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            SmallChange = 1,
            LargeChange = 10,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            Width = 140,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(metronomeFlyoutVolumeSlider, "Click volume");
        metronomeFlyoutVolumeReadout = StudioUi.Caption(string.Empty, 11, StudioTheme.Text);
        metronomeFlyoutVolumeReadout.MinWidth = 36;
        metronomeFlyoutVolumeSlider.ValueChanged += (_, _) =>
        {
            metronomeFlyoutVolumeReadout.Text = string.Create(CultureInfo.InvariantCulture, $"{metronomeFlyoutVolumeSlider.Value:0}%");
            if (!synchronizingMetronomeControls)
            {
                // The engine hears it at once; the settings file is written when the flyout closes.
                preferences.MetronomeVolume = Math.Round(metronomeFlyoutVolumeSlider.Value) / 100.0;
                ApplyMetronomeToEngine();
            }
        };
        metronomeFlyoutCountInComboBox = new ComboBox
        {
            ItemsSource = MetronomeCountInChoices.All.Select(choice => choice.Label).ToArray(),
            MinWidth = 110,
        };
        ToolTip.SetTip(metronomeFlyoutCountInComboBox, "Bars of clicks before a recording starts from a stopped transport. Nothing is recorded during the count-in.");
        metronomeFlyoutCountInComboBox.SelectionChanged += (_, _) =>
        {
            if (!synchronizingMetronomeControls && metronomeFlyoutCountInComboBox.SelectedIndex >= 0)
            {
                SetMetronomeCountIn(MetronomeCountInChoices.All[metronomeFlyoutCountInComboBox.SelectedIndex].Bars);
            }
        };

        var flyoutPanel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(4),
            Children =
            {
                StudioUi.Caption("Metronome", 12, StudioTheme.Text, FontWeight.SemiBold),
                metronomeFlyoutEnabledCheckBox,
                StudioUi.Row(8, StudioUi.Caption("Volume", 11), metronomeFlyoutVolumeSlider, metronomeFlyoutVolumeReadout),
                StudioUi.Row(8, StudioUi.Caption("Count-in", 11), metronomeFlyoutCountInComboBox),
                metronomeFlyoutOnlyWhileRecordingCheckBox,
            },
        };
        var metronomeFlyout = new Flyout { Content = flyoutPanel, Placement = PlacementMode.Bottom };
        metronomeFlyout.Opening += (_, _) => SynchronizeMetronomeControls();
        metronomeFlyout.Closed += (_, _) => PersistMetronomePreferences();
        return metronomeFlyout;
    }

    /// <summary>Makes the button, the flyout and the engine agree with the preferences.</summary>
    private void ApplyMetronomePreference()
    {
        preferences.MetronomeVolume = Math.Clamp(preferences.MetronomeVolume, 0, 1);
        preferences.MetronomeCountInBars = Math.Clamp(preferences.MetronomeCountInBars, 0, StudioPreferences.MaximumCountInBars);
        SynchronizeMetronomeControls();
        ApplyMetronomeToEngine();
    }

    private void SynchronizeMetronomeControls()
    {
        synchronizingMetronomeControls = true;
        if (metronomeToggle is not null)
        {
            metronomeToggle.IsChecked = preferences.MetronomeEnabled;
            metronomeToggle.Foreground = preferences.MetronomeEnabled ? StudioTheme.Accent : StudioTheme.Text;
        }
        if (metronomeFlyoutEnabledCheckBox is not null)
        {
            metronomeFlyoutEnabledCheckBox.IsChecked = preferences.MetronomeEnabled;
        }
        if (metronomeFlyoutOnlyWhileRecordingCheckBox is not null)
        {
            metronomeFlyoutOnlyWhileRecordingCheckBox.IsChecked = preferences.MetronomeOnlyWhileRecording;
        }
        if (metronomeFlyoutVolumeSlider is not null && metronomeFlyoutVolumeReadout is not null)
        {
            metronomeFlyoutVolumeSlider.Value = Math.Round(preferences.MetronomeVolume * 100);
            metronomeFlyoutVolumeReadout.Text = string.Create(CultureInfo.InvariantCulture, $"{metronomeFlyoutVolumeSlider.Value:0}%");
        }
        if (metronomeFlyoutCountInComboBox is not null)
        {
            metronomeFlyoutCountInComboBox.SelectedIndex = Math.Max(0, MetronomeCountInChoices.All.ToList().FindIndex(choice => choice.Bars == preferences.MetronomeCountInBars));
        }
        synchronizingMetronomeControls = false;
    }

    /// <summary>Tells the live engine whether to click right now: on, and either always or while Rec is engaged.</summary>
    private void ApplyMetronomeToEngine() =>
        liveAudioSession.Engine.ConfigureMetronome(
            preferences.MetronomeEnabled && (!preferences.MetronomeOnlyWhileRecording || RecordEngaged),
            preferences.MetronomeVolume);

    private void ToggleMetronome() => SetMetronomeEnabled(!preferences.MetronomeEnabled);

    private void SetMetronomeEnabled(bool metronomeEnabled)
    {
        preferences.MetronomeEnabled = metronomeEnabled;
        ApplyMetronomePreference();
        PersistMetronomePreferences();
        SetStatus(StatusSeverity.Informational, !metronomeEnabled
            ? "metronome off. the band will self-report its own timekeeping."
            : preferences.MetronomeOnlyWhileRecording
                ? "metronome on while recording: every beat gets a click, every downbeat a louder one. playback stays quiet."
                : "metronome on: every beat gets a click, every downbeat a louder one. the click is never bounced, only felt.");
    }

    private void SetMetronomeOnlyWhileRecording(bool onlyWhileRecording)
    {
        preferences.MetronomeOnlyWhileRecording = onlyWhileRecording;
        ApplyMetronomePreference();
        PersistMetronomePreferences();
        SetStatus(StatusSeverity.Informational, onlyWhileRecording
            ? "metronome clicks only while Rec is engaged. playback is now a click-free workplace."
            : "metronome clicks whenever it is on, recording or not.");
    }

    private void SetMetronomeCountIn(int countInBars)
    {
        preferences.MetronomeCountInBars = Math.Clamp(countInBars, 0, StudioPreferences.MaximumCountInBars);
        ApplyMetronomePreference();
        PersistMetronomePreferences();
        SetStatus(StatusSeverity.Informational, preferences.MetronomeCountInBars == 0
            ? "count-in off: recording starts the moment you press play. no runway, no onboarding."
            : $"count-in: {preferences.MetronomeCountInBars} bar(s) of clicks before a recording starts from a stop. nothing is recorded until the downbeat.");
    }

    private void PersistMetronomePreferences()
    {
        try
        {
            preferences.SaveMetronome();
        }
        catch (Exception persistenceRemediationOpportunity) when (persistenceRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Warning, $"metronome settings could not be saved: {persistenceRemediationOpportunity.Message}");
        }
    }

    /// <summary>
    /// Bars to count in before starting the transport now: the count-in setting while Rec is
    /// engaged and nothing plays yet, otherwise 0. Clicks during a count-in sound even with the
    /// metronome off.
    /// </summary>
    private int RecordingCountInBars()
    {
        if (!RecordEngaged || preferences.MetronomeCountInBars <= 0)
        {
            return 0;
        }
        var transportSnapshot = liveAudioSession.Engine.Transport;
        return PlaybackToggle.IsAudiblyPlaying(transportSnapshot) || transportSnapshot.CountingIn
            ? 0
            : Math.Min(preferences.MetronomeCountInBars, StudioPreferences.MaximumCountInBars);
    }

    private static int CountInBeatsLeft(TransportSnapshot transportSnapshot) =>
        (transportSnapshot.CountInStepsRemaining + MetronomeClickSubstrate.StepsPerBeat - 1) / MetronomeClickSubstrate.StepsPerBeat;

    private void AnnounceCountIn(int countInBars, string destination) =>
        SetStatus(StatusSeverity.Failure, $"counting in {countInBars} bar(s), then recording {destination}. tap your foot; it is the only unbilled hour today.");
}
