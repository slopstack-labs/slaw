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
- Installed Linux VST3s live in `/usr/lib/vst3`; real-plugin tests skip when they are absent.
- Commits go straight to `main` (the user authorized it), in the slaw commit voice, ending with the `Co-Authored-By` trailer.

## Where things live

| Area | Files |
|------|-------|
| Project model, notation, patches | `src/Slaw/Session/` — `SlawProject.cs`, `NotationResolver.cs`, `ClipNoteEditor.cs`, `ProjectPatchApplier.cs`, `ProjectAlignmentRemediator.cs`, `ClipAutomation.cs`, `FreeTimeline.cs`, `AudioTakeAssembler.cs` |
| Engine and devices | `src/Slaw/Synthesis/` — `SequencerEngine.cs`, `InstrumentVoiceSynthesizer.cs`, `EffectProcessors.cs`, `DeviceCatalog.cs`, `OfflineArrangementRenderer.cs` |
| VST3 hosting | `src/Slaw/Plugins/` |
| MCP | `src/Slaw/Mcp/` — `SlawMcpToolset.cs` (instructions, every tool), `ProtocolStandardOutput.cs` |
| LLM prompts and workflows | `src/Slaw/Workflows/PromptLibrary.cs`, `ProductionWorkflows.cs` |
| Studio UI | `src/Slaw/Desktop/` — `StudioWindow*.cs` partials, `DetailView*.cs`, views, `StudioShortcuts.cs` |
| Audio/MIDI I/O | `src/Slaw/Playback/` |

## Keep these in sync

Several places describe the same thing to different readers (humans, the autopilot LLM, MCP agents). When you change the left column, update **every** item on the right in the same change, or the next agent will be misled. Agents using the MCP have never seen this code; the tool text is all they get.

| When you change… | …also update |
|---|---|
| **An MCP tool** (add, rename, new argument, new behaviour) in `SlawMcpToolset.cs` | the tool's description and `inputSchema`; `ServerInstructions` if the workflow changes; `get_catalog` text if vocabulary changes; README "MCP" table; `McpServerTests`; `PromptLibrary` if the patch format changed |
| **`apply_patch` keys** (`ProjectPatchApplier`) | `apply_patch` description; autopilot patch prompt in `PromptLibrary`; `ProjectChangeSummarizer` (so the result says what changed); README project-format/MCP sections; tests in `AutopilotPatchTests`/`AutomationTests` |
| **Pattern notation** (`NotationResolver`, `ClipNoteEditor`, `CompactPatternJsonConverter`) | `ServerInstructions`; `get_catalog` pattern line; both pattern descriptions in `PromptLibrary` (composition prompt and patch prompt); `ProjectAlignmentRemediator` (must not strip valid syntax); README "Project format"; notation tests |
| **Project format** (`SlawProject` fields) | remediator (validate, migrate old files); serializer round-trip; `PromptLibrary.DescribeProjectForPatching` (this is also `get_project`'s listing); `ProjectChangeSummarizer`; `apply_patch`; README "Project format"; open every `examples/*.slaw` once to check they still load and render |
| **Devices** (instrument or effect in `DeviceCatalog` / `EffectProcessor.Create` / `InstrumentVoiceSynthesizer.SupportedInstruments`) | display name and description in `DeviceCatalog`; remediator synonyms (`ResolveInstrument`/`ResolveEffect`); README instruments/effects tables. Browser, knobs, `get_catalog` and automation targets read the catalog automatically — check they do |
| **Clip envelope targets** (`ClipAutomation`) | `set_automation` description; `get_catalog` envelope paragraph; `PromptLibrary` automation bullet; README "Automation" table; engine `CompiledEnvelopes` |
| **Timeline / arrangement behaviour** (`FreeTimeline`, engine transport) | `ServerInstructions` (agents struggle most here: units are sixteenth steps, 16 per bar, 0-based in arguments, 1-based bars in listings); `apply_patch`/`transport`/`render` descriptions; README; tests |
| **Keyboard shortcuts** (`StudioShortcuts` table) | nothing else for the F1 sheet (generated from the table); README "Keyboard" section; tooltips that mention the key |
| **CLI flags** (`CommandLineIntentParser`, `Program.cs`) | the flag lists in `CommandLineIntentParser`; `--help` text; README usage tables |
| **Settings** (`StudioPreferences`, `SettingsWindow*`) | README "Settings" table |
| **LLM prompts** (`PromptLibrary`) | keep them short: the default model is a 3B local one; examples beat prose |

After any change to MCP-facing text, re-run a cold-start check: give a fresh agent (no repo access) only the tool list and a small task on a scratch project through `slaw mcp`, and fix whatever it trips over.

## Rules learned the hard way

- **Nothing may write to stdout under `slaw mcp` except the protocol.** VST3 plugins print to fd 1 (OB-Xf does). `slaw mcp` claims stdout first thing (`ProtocolStandardOutput.Claim`) and points fd 1 at stderr. Code that runs while a project loads or is remediated must never instantiate a plugin: use `Vst3ParameterDirectory.Peek`, not `TryGet`/`Get`.
- **Never load plugins or touch the disk on the audio thread.** Use the background pools (`PluginInstancePool.TryGet`, `SampleEditProcessor.TryGetProcessed`) and render silence until ready. No per-block allocations in `MixTracks` or effect `Process`.
- **Effects are stereo** (`Process(left, right, …)`, with a mono fast path). Keep mono sources' levels identical to before when changing pan or gain laws.
- **Envelopes and effect slots are indexed by chain position**: when moving, inserting or removing effects, remap lanes with `ClipAutomation.MoveEffectSlot` / `ForgetEffectSlot` / `MakeRoomForEffectSlot`.
- **Edits go through `StudioDocument`** (`Edit`, or `BeginEdit`/`CommitEdit` around a gesture) so they are undoable, reach the live engine and refresh the views. Use `EditScope.Structure` when controls must be rebuilt.
- **Determinism**: offline renders of the same project must produce the same bytes (there is a test). Use the seeded `Random`s, never `Random.Shared`, in synthesis.

## Voice

- README, CONTRIBUTING, commit messages, status texts and UI captions: deadpan corporate parody, with jokes specific to slaw's own features.
- **Machine-facing text is literal first.** MCP tool descriptions, `ServerInstructions`, schema docs and error messages must be precise and plain, and a tool result must state what happened in plain words ("removed track Kick", "placed 3 timeline clips"). A joke may follow the fact, never replace it. Agents parse these.
