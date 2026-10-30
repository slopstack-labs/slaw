# CLAUDE.md

slaw is a real, working DAW (C#/.NET 8, Avalonia UI) wrapped in slopstack-labs corporate parody. The code works; the prose jokes. Read `CONTRIBUTING.md` for the house style (tokenmaxxed identifiers, `remediationOpportunity` for exceptions, LinkedIn-voice commit messages).

## Build, test, run

```sh
dotnet build src/Slaw                              # TreatWarningsAsErrors is on: a warning fails the build
dotnet test tests/Slaw.Tests                       # must stay green; a few VST3 tests skip without SLAW_TEST_VST3_DIR
dotnet publish src/Slaw/Slaw.csproj -c Release -o out   # what run.sh / the user's studio runs (run.sh only builds if out/ is missing)
dotnet src/Slaw/bin/Debug/net8.0/slaw.dll mcp song.slaw # headless MCP server over stdio, autosaves every edit
```

- The user often has `slaw studio --mcp` running on `127.0.0.1:47115`. Never kill it, never overwrite `out/` while it runs, and don't change their open project without saying so (back it up via `get_project` `format: "json"` first).
- Verify audio changes by rendering headlessly (`render` through `slaw mcp`, then measure the WAV). Verify UI changes by rendering the real `StudioWindow` with Avalonia.Headless in a scratch console project whose `AssemblyName` is `Slaw.Tests` (that is what `InternalsVisibleTo` allows) and looking at the PNG.
- macOS live output (`MacAudioQueueAudioSink`) cannot be heard from a sandboxed shell: Core Audio refuses with OSStatus -66681 (`afplay` too). Test it unsandboxed with a scratch harness and time the writes (1 s of audio should take about 1 s).
- Installed Linux VST3s live in `/usr/lib/vst3`; real-plugin tests skip when they are absent.
- Commits go straight to `main` (the user authorized it), in the slaw commit voice, ending with the `Co-Authored-By` trailer.

## The model in one paragraph

Two views, like Ableton Live. The **Session** holds clips by scene (`SlawTrack.Clips`, one slot per scene): loops to jam with and launch. The **Arrangement** is the song: one linear timeline (`SlawProject.Timeline`, see `FreeTimeline.cs`) of clips placed on a track at a start step for a length, in sixteenth-note steps, 16 per bar; a clip's pattern loops to fill its length. Only the arrangement is played by Play in the Arrangement view, bounced and rendered. Scenes are **not** the song order: `SlawProject.Arrangement` (a scene sequence) is legacy input only — remediation flattens it onto an empty timeline and drops it, and it is never written back. While the arrangement plays, launching a session clip takes that track over from the next bar (`TrackRuntimeState.OverridesArrangement`) until Back to Arrangement.

## Where things live

| Area | Files |
|------|-------|
| Project model, notation, patches | `src/Slaw/Session/` — `SlawProject.cs`, `NotationResolver.cs`, `ClipNoteEditor.cs`, `ProjectPatchApplier.cs`, `ProjectAlignmentRemediator.cs`, `ClipAutomation.cs`, `FreeTimeline.cs`, `AudioTakeAssembler.cs` |
| Engine and devices | `src/Slaw/Synthesis/` — `SequencerEngine.cs`, `InstrumentVoiceSynthesizer.cs`, `EffectProcessors.cs`, `DeviceCatalog.cs`, `OfflineArrangementRenderer.cs` |
| VST3 hosting | `src/Slaw/Plugins/` |
| SoundFonts | `src/Slaw/SoundFonts/` (SF2 parser, cache, voice renderer), `Session/SoundFontDeviceOperations.cs`, MIDI import in `Session/MidiFileImporter.cs` |
| MCP | `src/Slaw/Mcp/` — `SlawMcpToolset.cs` (instructions, every tool), `ProtocolStandardOutput.cs` |
| LLM prompts and workflows | `src/Slaw/Workflows/PromptLibrary.cs`, `ProductionWorkflows.cs` |
| Studio UI | `src/Slaw/Desktop/` — `StudioWindow*.cs` partials (`StudioWindow.Timeline.cs` is the Arrangement's transport and keys), `SessionGridView.cs`, `ArrangementView.cs`, `DetailView*.cs`, `StudioShortcuts.cs`. Panels and the second window: `StudioWindow.Windows.cs`, `StudioWorkspacePane.cs`, `StudioSecondWindow.cs` |
| Audio/MIDI I/O | `src/Slaw/Playback/` — live output per OS: `MacAudioQueueAudioSink` (Core Audio), `WindowsWaveOutAudioSink`, `PipedProcessAudioSink` (Linux, or a player picked in Settings); recording: `MacAudioQueueAudioInput`, `WindowsWaveInAudioInput`, `PipedProcessAudioInput` |
| Installers | `installer/` — Windows: `slaw.wxs` (MSI), `bundle.wxs` + `SlawSetup/` (setup.exe with turntables, WiX v5), `New-InstallerAssets.ps1`; macOS: `installer/macos/` (`build-dmg.sh`, `dmg-settings.py`, `render-assets.swift`). Built only by the manual workflows `release-installer.yml` and `release-macos.yml`. Every release workflow is `workflow_dispatch` only (a pushed tag publishes nothing); only `ci.yml` runs on its own |

## Keep these in sync

Several places describe the same thing to different readers (humans, the autopilot LLM, MCP agents). When you change the left column, update **every** item on the right in the same change, or the next agent will be misled. Agents using the MCP have never seen this code; the tool text is all they get.

| When you change… | …also update |
|---|---|
| **An MCP tool** (add, rename, new argument, new behaviour) in `SlawMcpToolset.cs` | the tool's description and `inputSchema`; `ServerInstructions` if the workflow changes; `get_catalog` text if vocabulary changes; README "MCP" table; `McpServerTests`; `PromptLibrary` if the patch format changed |
| **`apply_patch` keys** (`ProjectPatchApplier`) | `apply_patch` description; autopilot patch prompt in `PromptLibrary`; `ProjectChangeSummarizer` (so the result says what changed); README project-format/MCP sections; tests in `AutopilotPatchTests`/`AutomationTests` |
| **Pattern notation** (`NotationResolver`, `ClipNoteEditor`, `CompactPatternJsonConverter`) | `ServerInstructions`; `get_catalog` pattern line; both pattern descriptions in `PromptLibrary` (composition prompt and patch prompt); `PatternLint` (warnings for syntax that parses but misleads, e.g. `A3+C4+E4@0.7` softening only E4 — `@`/`:` bind per note); `ProjectAlignmentRemediator` (must not strip valid syntax); README "Project format" and MCP sections; notation tests |
| **Project format** (`SlawProject` fields) | remediator (validate, migrate old files); serializer round-trip; `PromptLibrary.DescribeProjectForPatching` (this is also `get_project`'s listing); `ProjectChangeSummarizer`; `apply_patch`; README "Project format"; open every `examples/*.slaw` once to check they still load and render |
| **Devices** (instrument or effect in `DeviceCatalog` / `EffectProcessor.Create` / `InstrumentVoiceSynthesizer.SupportedInstruments`) | display name and description in `DeviceCatalog`; remediator synonyms (`ResolveInstrument`/`ResolveEffect`); README instruments/effects tables. Browser, knobs, `get_catalog` and automation targets read the catalog automatically — check they do |
| **Clip envelope targets** (`ClipAutomation`) | `set_automation` description; `get_catalog` envelope paragraph; `PromptLibrary` automation bullet; README "Automation" table; engine `CompiledEnvelopes` |
| **Arrangement behaviour** (`FreeTimeline`, engine transport, session launches over the arrangement, the legacy `arrangement` scene-sequence key) | `ServerInstructions` (agents struggle most here: `start`/`length`/`step` arguments are 0-based sixteenth steps, 16 per bar; `bar`/`bars` arguments are 1-based; listings print both via `FreeTimeline.DescribeSpan`; ends are exclusive; scenes are not the song order); the `arrangement_*` tools, `apply_patch`/`transport`/`render` descriptions; `PromptLibrary` (patch prompt, `DescribeProjectForPatching`, `DescribeArrangement`); remediator migration; README "Arrangement" and MCP sections; `ArrangementTests`, `McpArrangementToolTests` |
| **Patch or tool results** (`ProjectChangeSummarizer`, `DescribeOutcome`, warnings, remediation ledger entries in `ProjectAlignmentRemediator`/`FreeTimeline.Remediate`) | keep them literal: what changed, with ids, positions and envelopes, then `warnings:` for anything ignored, clamped or unknown. Report each change once (a removed track's clips are counted on its "removed track" line, not again as a remediation). Never adjust silently; report only what this call caused (`NewRemediations`). No parody wording in this channel — the CLI may dress ledger entries up for humans |
| **Level help** (`render levels:true`, `get_catalog` instrument levels) | instrument levels are measured at runtime (`BuiltInInstrumentLevels`), so new or retuned instruments update themselves; the balance warnings use a 12 dB distance from the median track — keep README "MCP" in step if that changes |
| **Sidechains or the master chain** (`SidechainRouting`, `EffectDevice.SidechainSource`, `MasterChainParameters.Effects`, engine order in `MixTracks`) | `ServerInstructions` "SIDECHAIN AND MASTER" paragraph; `add_effect` and `apply_patch` descriptions and schema; `get_catalog` sidechain line (`PromptLibrary.DescribeDeviceCatalog`); `PromptLibrary` patch prompt bullet and listing; `ProjectChangeSummarizer`; remediator (`RemediateSidechains`, `RemediateEffectChain` for the master); README "Sidechains and the master chain"; `SidechainTests` |
| **Keyboard shortcuts / commands** (`StudioShortcuts` table) | nothing else for the F1 sheet or the menu bar's shortcut labels (both read the table); the menu item in `StudioWindow.Menu.cs` if the command should be reachable from a menu; README "Keyboard" and "Window layout" sections; tooltips that mention the key |
| **Installers** (`installer/`, `release-installer.yml`, `release-macos.yml`) | README "Installers"; the DMG icon positions and window size live in both `render-assets.swift` and `dmg-settings.py`; keep WiX at v5 unless the maintenance fee is settled (v6+ charges revenue-earning organizations); **new DLLs or other files the app needs at runtime** must be added to the setup in the same change: the MSI installs only the files `slaw.wxs` names (`slaw.exe` and a few others), so anything `dotnet publish -r win-x64` leaves beside `slaw.exe` instead of folding it in needs its own `<Component>` there, or the installed app fails while the dev build works. DLLs the setup UI (`SlawSetup/`) needs go in `bundle.wxs` as a `<Payload>`. The macOS app bundles the whole publish folder, so it picks them up by itself |
| **CLI flags** (`CommandLineIntentParser`, `Program.cs`) | the flag lists in `CommandLineIntentParser`; `--help` text; README usage tables |
| **Settings** (`StudioPreferences`, `SettingsWindow*`) | README "Settings" table |
| **LLM prompts** (`PromptLibrary`) | keep them short: the default model is a 3B local one; examples beat prose. The default co-pilot model (qwen2.5:3b) copies example values literally: label each example with the request it answers, give examples for other songs with obviously different ids/values, and say "never copy". Measure prompt changes against the real local model (Ollama) with a scratch harness that runs the workflow and reports what each patch changed, not by reading the prompt |

After any change to MCP-facing text, re-run a cold-start check: give a fresh agent (no repo access) only the tool list and a small task on a scratch project through `slaw mcp`, and fix whatever it trips over.

## Rules learned the hard way

- **Nothing may write to stdout under `slaw mcp` except the protocol.** VST3 plugins print to fd 1 (OB-Xf does). `slaw mcp` claims stdout first thing (`ProtocolStandardOutput.Claim`) and points fd 1 at stderr. Code that runs while a project loads or is remediated must never instantiate a plugin: use `Vst3ParameterDirectory.Peek`, not `TryGet`/`Get`.
- **The plugin host thread is the plugins' UI thread** (`PluginHostThread`): every load, controller call and editor (IPlugView) call happens there. On Windows it pumps window messages; on Linux it is the IRunLoop VST3 editors are lent (`Vst3LinuxRunLoop`): one `poll()` over a wake-up eventfd, the editors' shared Xlib connection (`Vst3LinuxEditorWindow`) and every descriptor/timer plugins register. Never block it for long, and never call a plugin's controller or view from another thread; queue work with `PluginHostThread.InvokeAsync`.
- **Never load plugins or touch the disk on the audio thread.** Use the background pools (`PluginInstancePool.TryGet`, `SampleEditProcessor.TryGetProcessed`, `SoundFontLibrary.TryGetPreset`) and render silence until ready; bounces and auditions use the blocking `Load*` variants. No per-block allocations in `MixTracks` or effect `Process`.
- **Sidechains:** tracks are mixed in `SidechainRouting.ProcessingOrder` (sources first); a source is tapped after its effects and before its fader, mute and solo, and keyed effects get it through `EffectProcessingContext.SidechainLeft/Right` for the same frames. VST3 aux buses may only be (de)activated while the plugin is inactive: `Vst3PluginInstance.RequestSidechain` does it on the plugin host thread (synchronously for offline instances, so bounces stay deterministic). Master effects run on the sum inside `MixTracks`, per chunk, before `MasterBusProcessor`.
- **Moving a control between windows** crashes the studio ("wrong LayoutManager") if its old window still has layout queued for it. Take it out, call `UpdateLayout()` on both, then attach it, as `ArrangeWorkspaces` does. Window-layout preferences go through `SaveWindowLayout`, because the live `StudioPreferences` may carry command-line overrides that must not reach the disk.
- **Effects are stereo** (`Process(left, right, …)`, with a mono fast path). Keep mono sources' levels identical to before when changing pan or gain laws.
- **Envelopes and effect slots are indexed by chain position**: when moving, inserting or removing effects, remap lanes with `ClipAutomation.MoveEffectSlot` / `ForgetEffectSlot` / `MakeRoomForEffectSlot`.
- **Envelope positions differ by clip kind.** Session clips: steps 0..pattern length, looping with the pattern. Arrangement clips: steps since the clip's start, 0..`LengthSteps`, once (`TrackRuntimeState.EnvelopeStep`, separate from the pattern step `CurrentClipStep`). Validate and tidy arrangement envelopes against `LengthSteps`, never the pattern. Whenever session material becomes an arrangement clip longer than its pattern, copy it with `FreeTimeline.ArrangementCopyOf` (it tiles the envelopes so the result sounds as it looped). Envelope values are absolute: `gain` replaces the fader, it does not scale it.
- **Filters are zero-delay (trapezoidal) state-variable filters**, stable up to Nyquist (`7b8bac1` replaced the Chamberlin design that blew up above roughly a quarter of the sample rate). Keep any new filter unconditionally stable; there is a test with cutoffs near Nyquist.
- **Edits go through `StudioDocument`** (`Edit`, or `BeginEdit`/`CommitEdit` around a gesture) so they are undoable, reach the live engine and refresh the views. Use `EditScope.Structure` when controls must be rebuilt.
- **Determinism**: offline renders of the same project must produce the same bytes (there is a test). Use the seeded `Random`s, never `Random.Shared`, in synthesis.

## Voice

- README, CONTRIBUTING, commit messages, status texts and UI captions: deadpan corporate parody, with jokes specific to slaw's own features.
- **Machine-facing text is literal first.** MCP tool descriptions, `ServerInstructions`, schema docs and error messages must be precise and plain, and a tool result must state what happened in plain words ("removed track Kick", "placed 3 timeline clips"). A joke may follow the fact, never replace it. Agents parse these.
