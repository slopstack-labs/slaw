using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SlopstackLabs.Slaw.Plugins;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// Signal routing in the device chain: the "Sidechain:" picker on effects that listen
/// to one, and the master chain — effects on the sum of every track, after the Master Bus device.
/// </summary>
internal sealed partial class DetailView
{
    /// <summary>
    /// "Sidechain: Kick" for the ducker and for VST3 effects whose plugin has a sidechain
    /// input; null for everything else. <paramref name="owningTrackId"/> is null on the master.
    /// Tracks that would close a loop are not offered.
    /// </summary>
    private Control? ComposeSidechainSelector(EffectDevice effectDevice, string? owningTrackId, string slotKey)
    {
        var listens = effectDevice.Type == SidechainRouting.DuckEffectType ||
                      (effectDevice.Type == DeviceTypes.Vst3 &&
                       (effectDevice.SidechainSource is not null || PluginInstancePool.Live?.Peek(slotKey, effectDevice.Plugin)?.HasSidechainInput == true));
        if (!listens)
        {
            return null;
        }
        var project = document.Project;
        var candidateTracks = project.Tracks
            .Where(track => track.Id == effectDevice.SidechainSource || !SidechainRouting.WouldCreateCycle(project, owningTrackId, track.Id))
            .ToList();
        var sourceSelector = new ComboBox
        {
            ItemsSource = candidateTracks.Select(track => track.Name).Prepend("(none)").ToList(),
            SelectedIndex = effectDevice.SidechainSource is { } sourceId ? candidateTracks.FindIndex(track => track.Id == sourceId) + 1 : 0,
            FontSize = 11,
            MinWidth = 110,
            Padding = new Thickness(6, 1),
        };
        ToolTip.SetTip(sourceSelector, "The track that keys this effect. It is heard after its own effects and before its fader, so a muted track still keys.");
        sourceSelector.SelectionChanged += (_, _) =>
        {
            var pickedSource = sourceSelector.SelectedIndex > 0 ? candidateTracks[sourceSelector.SelectedIndex - 1] : null;
            if (pickedSource?.Id == effectDevice.SidechainSource)
            {
                return;
            }
            var effectName = PluginDeviceOperations.DeviceName(effectDevice);
            document.Edit(pickedSource is null ? $"removed the sidechain of {effectName}" : $"keyed {effectName} from {pickedSource.Name}",
                _ => effectDevice.SidechainSource = pickedSource?.Id, EditScope.Structure);
        };
        return StudioUi.Row(4, StudioUi.Caption("Sidechain", 10), sourceSelector);
    }

    // ── Master chain ──────────────────────────────────────────────────────────

    private Control ComposeMasterEffectDevice(int effectIndex)
    {
        var masterEffects = document.Project.Master.Effects;
        var effectDevice = masterEffects[effectIndex];
        var effectName = PluginDeviceOperations.DeviceName(effectDevice);
        var enabledToggle = new CheckBox { IsChecked = effectDevice.Enabled, Padding = new Thickness(2, 0), MinWidth = 0 };
        ToolTip.SetTip(enabledToggle, "Device on/off");
        enabledToggle.IsCheckedChanged += (_, _) =>
        {
            if (effectDevice.Enabled != (enabledToggle.IsChecked == true))
            {
                document.Edit($"{(enabledToggle.IsChecked == true ? "enabled" : "bypassed")} the master {effectName}",
                    _ => effectDevice.Enabled = enabledToggle.IsChecked == true);
            }
        };
        void MoveMasterEffect(int offset)
        {
            var destinationIndex = effectIndex + offset;
            if (destinationIndex < 0 || destinationIndex >= masterEffects.Count)
            {
                return;
            }
            document.Edit("reordered the master chain", _ =>
            {
                masterEffects.RemoveAt(effectIndex);
                masterEffects.Insert(destinationIndex, effectDevice);
            }, EditScope.Structure);
        }
        var titleRow = StudioUi.Row(2,
            enabledToggle,
            StudioUi.Button("◀", () => MoveMasterEffect(-1), "Move left", compact: true),
            StudioUi.Button("▶", () => MoveMasterEffect(1), "Move right", compact: true),
            StudioUi.Button("✕", () => document.Edit($"removed the master {effectName}", _ => masterEffects.RemoveAt(effectIndex), EditScope.Structure),
                "Remove device", compact: true));
        if (effectDevice.Type == DeviceTypes.Vst3 && effectDevice.Plugin is { } editablePlugin)
        {
            titleRow.Children.Add(ComposePluginEditorButton(editablePlugin, SequencerEngine.MasterEffectSlotOwner, effectIndex));
        }
        var slotKey = PluginInstancePool.EffectSlot(SequencerEngine.MasterEffectSlotOwner, effectIndex);
        Control titleControls = ComposeSidechainSelector(effectDevice, owningTrackId: null, slotKey) is { } sidechainSelector
            ? new StackPanel { Spacing = 4, Children = { titleRow, sidechainSelector } }
            : titleRow;

        var knobs = DeviceCatalog.EffectParametersFor(effectDevice.Type).Select(parameterSpecification => ComposeParameterKnob(
            parameterSpecification,
            () => effectDevice.Parameters,
            $"master/fx{effectIndex}/{parameterSpecification.Key}",
            $"master {DeviceCatalog.EffectDisplayName(effectDevice.Type)} {parameterSpecification.Label}")).ToList();
        if (effectDevice.Type == DeviceTypes.Vst3 && effectDevice.Plugin is { } effectPlugin)
        {
            knobs = new List<Control> { ComposePluginParameterPanel(effectPlugin, () => effectDevice.Parameters, $"master/fx{effectIndex}", slotKey) };
        }
        var devicePanel = ComposeDevicePanel($"Master · {effectName}", titleControls, knobs, StudioTheme.Accent);
        ToolTip.SetTip(devicePanel, $"On the master: processes the sum of every track, before the Master Bus drive/lowpass/delay/gain. {DeviceCatalog.EffectDescription(effectDevice.Type)}".Trim());
        devicePanel.Opacity = effectDevice.Enabled ? 1 : 0.55;
        return devicePanel;
    }

    private Control ComposeAddMasterEffectButton()
    {
        var addEffectButton = new Button
        {
            Content = "+ Master FX",
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(10, 6),
        };
        ToolTip.SetTip(addEffectButton, "Add an effect to the master chain: it processes the sum of every track");
        var effectMenu = new MenuFlyout();
        foreach (var effectType in DeviceCatalog.EffectTypes)
        {
            var effectMenuItem = new MenuItem { Header = DeviceCatalog.EffectDisplayName(effectType) };
            effectMenuItem.Click += (_, _) => AddMasterEffect(new EffectDevice { Type = effectType });
            effectMenu.Items.Add(effectMenuItem);
        }
        var pluginSubmenu = new MenuItem { Header = "VST3" };
        void Populate() => PopulatePluginMenu(pluginSubmenu.Items, instruments: false,
            pickedPlugin => AddMasterEffect(PluginDeviceOperations.CreateEffect(PluginDeviceOperations.ToReference(pickedPlugin))));
        Populate();
        effectMenu.Opening += (_, _) => Populate();
        effectMenu.Items.Add(pluginSubmenu);
        addEffectButton.Flyout = effectMenu;
        addEffectButton.IsEnabled = document.Project.Master.Effects.Count < ProjectAlignmentRemediator.MaximumEffectsPerTrack;
        return addEffectButton;
    }

    private void AddMasterEffect(EffectDevice addedEffect)
    {
        if (document.Project.Master.Effects.Count >= ProjectAlignmentRemediator.MaximumEffectsPerTrack)
        {
            return;
        }
        document.Edit($"added {PluginDeviceOperations.DeviceName(addedEffect)} to the master", project => project.Master.Effects.Add(addedEffect), EditScope.Structure);
    }
}
