# slaw
### *Beyond Deterministic Production*

> "We didn't reinvent the DAW. We asked an AI to guess what music sounds like."
> — slaw Engineering Blog, Issue 1 (Final)

**slaw** is a next-generation, AI-native, LLM-first digital audio workstation that leverages the full generative power of large language models to holistically transform your *vibe* into an audible waveform artifact — at inference speed, across every major foundational intelligence provider in the ecosystem.

Traditional DAWs are slow, opinionated, and constrained by decades of musical thinking. They have timelines. They have plugins. They expect you to know what a compressor does. slaw moves production to the inference layer, reasoning about your *intent* rather than your *arrangement* — eliminating the pedantic intermediate steps (rhythm, melody, taste) that have constrained the music production lifecycle for decades. The bottleneck is no longer your sound design. It's your willingness to hit play.

**Key differentiators:**
- Zero determinism — every bounce is a unique stakeholder experience
- Multi-provider intelligence substrate — route your production intent through Ollama, OpenAI, Google Gemini, Anthropic Claude, or any OpenAI-compatible server
- Zero samples shipped — everything is synthesized, because samples would imply someone recorded something on purpose. (Bring your own WAVs via the `sampler`. They are yours, and therefore your responsibility.)
- Blazing-fast time-to-tinnitus
- Clipping-as-a-feature architecture, responsibly neutralized by the hearing protection substrate
- Model-native waveform synthesis unconstrained by legacy notions of "audio format"
- Agentic arrangement pipeline (`--optimistic`) for synergistic human-AI co-production
- A native, Ableton-style session-view studio: clips, scenes, mixer strips, device chains, and an arrangement timeline, none of which you asked an AI to understand
- **Live editing**: a real-time engine streams audio continuously, so every note, knob, and whole-song AI rewrite is heard on the next step with no restart
- **AI Autopilot**: hand the model the entire project and an instruction, receive a full rewrite and a change summary. Tracks marked 🔒 *Protect from AI* are restored verbatim, because boundaries matter
- **Full Self-Producing (Beta)**: the Autopilot, in a loop, until you intervene
- An unsolicited AI co-pilot that interjects every few edits, on by default
- **You can turn the AI off.** Nobody at slopstack labs has ever turned it off
- Undo/redo for everything, including the AI's decisions
- AI mastering, AI tempo negotiation, AI track naming, and AI vibe checks by a world-renowned LinkedIn thought leader
- Fully on-premise inference available — your data, your model, your noise
- Radical shift-left audio delivery: bypass intermediate music theory layers and ship directly to the speakers

**slaw is the only DAW built on the insight that your track doesn't need to be *good* — it needs to be *shipped*.**

## Roadmap

See the [Circle of Fifths of Inevitability](ROADMAP.md).

## Contributing

See the [Contributor Excellence Framework](CONTRIBUTING.md).

## Platform support

slaw runs natively on **Linux, macOS, and Windows**. It is written in C# on .NET 8, and the studio is a desktop application built with [Avalonia](https://avaloniaui.net) 11.3, a cross-platform .NET UI toolkit. The UI is composed entirely in C# with no XAML, because declarative markup is a legacy constraint inherited from a less agentic era.

Dependencies: zero beyond the UI toolkit. Every sample is synthesized, every HTTP call is hand-rolled, and the synth engine and WAV writer are pure base class library.

`slaw play` auditions audio through whatever your host provides:

| Host | Player |
|------|--------|
| Linux | `pw-play`, `paplay`, `aplay`, or `ffplay` (first one found) |
| macOS | `afplay` |
| Windows | PowerShell `Media.SoundPlayer` |

The studio does not use these. It runs its own live engine and streams audio continuously to your host's audio substrate (`pw-cat`, `pacat`, `aplay`, or sox `play` on Linux/macOS; winmm waveOut on Windows).

## Requirements

- .NET 8 SDK to build
- **For the studio on Linux:** a desktop session (X11 or XWayland). The headless commands (`compose`, `render`, `play`, `master`, `vibe`, `new`) work anywhere, including CI/CD.
- **For `--provider=local`:** [Ollama](https://ollama.com) running locally with a model pulled
- **For `--provider=openai|google|claude`:** a valid API key surfaced via `--api-key` or the `SLAW_API_KEY` environment variable
- **For `--provider=compat`:** any OpenAI-compatible server (LM Studio, llama.cpp server, vLLM, OpenRouter, Groq, Mistral, ...) and its `--base-url`

## Build

```sh
dotnet build -c Release
```

Single-file binary:

```sh
dotnet publish src/Slaw -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
```

Supported runtime identifiers: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`. Release assets are named `slaw-linux-x64`, `slaw-linux-arm64`, `slaw-osx-x64`, `slaw-osx-arm64`, and `slaw-win-x64.exe`. Each is a single-file executable that is both the desktop studio and the CLI.

Or use the helper script for your shell, which builds if needed, starts Ollama, pulls the model, and runs slaw. With no arguments it opens the `studio` window:

```sh
# Linux / macOS
./run.sh [slaw args...]
MODEL=phi3 ./run.sh compose "lofi beat for productivity"
```

```powershell
# Windows
./run.ps1 [slaw args...]
$env:MODEL = "phi3"; ./run.ps1 compose "lofi beat for productivity"
```

The default `MODEL` is `llama3`.

## Usage

```sh
slaw                        # opens the desktop studio
slaw <command> [options]    # headless production pipeline
```

| Command | Description |
|---------|-------------|
| `slaw` / `slaw studio [project.slaw]` | Opens the desktop session-view studio window. Opens or creates the project. Add `--view arrangement`, `session`, or `sample` to choose the starting view. |
| `slaw compose "<vibe>" [-o out]` | Ask the model to produce audio from a vibe. Default mode writes `slop.wav`. |
| `slaw render <project.slaw> [-o out.wav]` | Deterministic offline bounce to 16-bit 44.1 kHz stereo WAV. |
| `slaw play <project.slaw\|file.wav>` | Render (if needed) and audition through the host player. |
| `slaw master <project.slaw> [-o out.slaw]` | AI mastering: the model chooses the master chain, saved into the project and bounced to WAV. |
| `slaw vibe <project.slaw>` | Vibe check. Returns `VIBE SCORE: N/10` and a short critique. |
| `slaw new [project.slaw]` | Scaffold a starter project (default `untitled.slaw`). |

| Flag | Default | Description |
|------|---------|-------------|
| `--provider` | `local` | Intelligence provider to route production intent through: `local`, `openai`, `claude`, `google`, `compat` |
| `--api-key` | — | API key for the selected provider (required unless `local` or `compat`; or set `SLAW_API_KEY`) |
| `--model` | provider-specific | Model to use (defaults: `llama3` / `gpt-4o` / `claude-opus-5-5` / `gemini-2.5-flash` / `default` for `compat`) |
| `--ollama` | `http://localhost:11434/api/generate` | Ollama API URL (only honoured with `--provider=local`) |
| `--base-url` | — | Base URL for `compat` (e.g. `http://localhost:1234/v1` for LM Studio), or to override `openai` |
| `-o` | see command | Output path |
| `--optimistic` | false | `compose`: engage agentic arrangement co-pilot |
| `--loop N` | 0 | `compose`: re-align LLM outputs with ground truth up to N times on JSON parse failure |
| `--force-iterate N` | 0 | `compose`: proactively enhance output quality for N cycles even on success |
| `--view` | `session` | `studio`: starting view, `session`, `arrangement`, or `sample` (the first sampler's editor) |
| `--no-copilot` | false | `studio`: disable the unsolicited AI co-pilot (why would you). Flags override saved Settings for one session |
| `--timeout N` | 300 | HTTP request timeout in seconds per LLM call (`0` = no timeout) |
| `--verbose` | false | Verbose debug output |

## Production Modalities

Everything in this section is the **headless production pipeline for CI/CD-native music delivery**: no window, just terminal spinners and a `.wav` at the end of the pipeline.

### Core Mode — *Frictionless Waveform Ideation*

Instructs the model to emit a WAV file as a hex string, primed with `52494646` ("RIFF"). slaw then discards whatever header the model wrote, synthesizes a compliant RIFF header, interprets the bytes as 8-bit unsigned PCM at 8 kHz, and engages the temporal upscaling substrate to loop the result to at least 4 seconds. Output goes to `slop.wav`. This is our flagship zero-abstraction, direct-to-tinnitus pipeline.

```sh
# Local inference via Ollama
slaw compose "lofi beat for productivity" --model phi3

# Leverage frontier intelligence for accelerated waveform misalignment
slaw compose "synergy anthem" --provider openai --api-key $OPENAI_API_KEY
slaw compose "synergy anthem" --provider claude --api-key $ANTHROPIC_API_KEY
slaw compose "synergy anthem" --provider google --api-key $GEMINI_API_KEY

# Any OpenAI-compatible server
slaw compose "synergy anthem" --provider compat --base-url http://localhost:1234/v1

slaw play slop.wav
```

### Optimistic Mode (`--optimistic`) — *Agentic Arrangement Co-Pilot*

Engages the model as an arrangement partner. It emits a structured `.slaw` JSON project, which slaw remediates (clamping values, mapping hallucinated instruments such as "warm analog bass" to `saw`, padding patterns) and bounces through the real synth engine. Writes `slop.slaw` and `slop.wav`.

```sh
slaw compose --optimistic "uplifting corporate house" --provider openai --api-key $OPENAI_API_KEY
```

### Loop Mode (`--loop N`) — *Closed-Loop Arrangement Remediation*

When the arrangement fails to parse, the error is fed back to the model for up to N self-correction cycles.

```sh
slaw compose --optimistic --loop 5 "lofi beat" --provider claude --api-key $ANTHROPIC_API_KEY
```

### Force Iterate Mode (`--force-iterate N`) — *Continuous Improvement Pipeline*

Engages N improvement cycles even on success. A working arrangement is a starting point, not an endpoint.

```sh
slaw compose --optimistic --force-iterate 3 "lofi beat" --model phi3
```

### Render Mode — *Legacy Compliance Mode*

`slaw render` performs a deterministic offline bounce of a `.slaw` project to 16-bit 44.1 kHz stereo WAV. It is the only deterministic component in slaw, retained for regulatory compliance.

```sh
slaw render song.slaw -o song.wav
```

### Mastering and Vibe Check

`slaw master` has the model choose the master chain (`gain`, `drive`, `lowpassHz`, `delaySteps`, `delayFeedback`, `delayMix`) along with a written rationale, which is saved into the project. `slaw vibe` returns a vibe score from "a world-renowned producer and LinkedIn thought leader".

```sh
slaw master song.slaw -o song-mastered.slaw
slaw vibe song.slaw
```

The master chain is: feedback delay, one-pole lowpass, `tanh` saturation, gain, and finally the **hearing protection substrate**, a live peak limiter at roughly -1 dBFS that no model or prompt can raise. Hallucinated audio is additionally played at reduced level.

## The Studio

`slaw` (or `slaw studio [project.slaw]`) opens a **session-oriented desktop DAW** on Linux, macOS, and Windows. It is built with Avalonia 11.3 and composed entirely in C#, with no XAML. The layout will be familiar to anyone who has used Ableton Live, and unfamiliar to anyone who has used a compressor. Before any inference has occurred, the AI co-pilot greets you with a hard-coded "It looks like you're making a beat. Would you like help with that?" line.

### The live engine

A real-time, sample-accurate sequencer runs in-process and streams audio continuously (Linux and macOS via a pipe into `pw-cat`, `pacat`, `aplay`, or sox `play`; Windows via the winmm waveOut API). **Every edit is heard on the next step, without stopping or restarting playback**: a note, a knob, the BPM, a mute, or an AI rewriting the entire song. The same engine renders offline bounces, so what you hear is what you export. The **hearing protection substrate** is now a live peak limiter at about -1 dBFS.

### Window layout

- **Toolbar** (top): New / Open… / Save / Bounce, Play/Stop, BPM, project title, the **Session / Arrangement** switch, a master **AI** switch, Settings (⚙), and a status chip showing the inference substrate ("● llama3 via Ollama" or "○ AI offline").
- **Session view**: tracks are columns, scenes are rows. Each cell is a clip slot with a ▶ launch triangle; empty slots show a stop square, and double-clicking one creates a clip. Launches are **quantized to the next bar**, like a real DAW. The right-most column launches whole scenes (▶ per scene, editable scene names; right-click for *append to arrangement*, *duplicate*, *delete*) and holds the master fader and **Stop all clips**. Every track column ends in a **mixer strip**: stop button, pan knob, vertical volume fader with dB readout, Mute / Solo, a 🔒 **Protect from AI** lock, delete, and "✨ Name" (AI rename).
- **Arrangement view**: the song as an ordered list of scene sections on a timeline with a lane per track. Each track has an editable header (name, M, S, 🔒 AI lock, volume slider). Each section header has a scene dropdown, −/+ repeats, ▶ play-from-here, move earlier/later, and remove. **+ Section** adds one. Click any block to open that track's clip for that scene in the piano roll and device chain below (the open clip is outlined wherever it appears in the timeline). Right-click a block for *Create clip*, *Edit clip*, *Delete clip*, or ✨ *Generate/Rewrite clip* with AI. Double-click a block to play from there. A green playhead sweeps the timeline.
- **Detail view** (bottom): the clip editor. Melodic tracks get a polyphonic **piano roll** (click to add notes or chords, drag right to hold, click a note to delete, wheel or ▲▼ to scroll octaves); drums get a step row. The keyboard strip along the left edge is playable: see **Note audition** below. Clip name, clip length (4-64 steps), Clear, ▶ Clip, and "✨ Rewrite" sit alongside. Next to the editor is the **device chain**: the instrument device with knobs (oscillators: Attack, Decay, Sustain, Release, Filter, Octave, Detune, AI Humanize; pluck: Length, Brightness, …; drums: Pitch, Decay, Tone, AI Humanize), then audio effects with knobs, on/off, reorder, remove, and **+ Effect**, and finally the **Master Bus** device (Drive, Lowpass, Delay time/feedback/mix, "✨ AI master"). Knobs the AI changed glow violet until you touch them.
- **Browser** (left, AI on): lists instruments (click to add a track) and effects (click to add to the selected track), plus the AI actions. A **Samples** section lists your own WAVs (see **Your own samples**).
- **AI panel** (right, AI on): the co-pilot transcript and the Autopilot chat box. See below.

Closing the window with unsaved changes is intercepted by the co-pilot on the first attempt ("close again to abandon your artistic vision").

### Note audition

On the piano roll's keyboard strip, click and hold a key to hear that note on the track's instrument, through its effects. Drag along the keyboard to glide between notes. Releasing fades the note over the instrument's release time (drums and plucks ring out). Placing a note in the grid also previews it for as long as the mouse is held.

### Your own samples

The `sampler` instrument ("Sampler") plays your own WAV files, pitched across the piano roll. C4 is the root note by default, i.e. original pitch. Knobs: Root Note, Transpose, Start, End (trim), Note Length (off = one-shot, on = follows the note length), Reverse, Attack, Release, Gain, Filter, AI Humanize.

- **Format**: uncompressed WAV only. 8/16/24/32-bit PCM and 32/64-bit float, any channel count (mixed to mono), any sample rate. An MP3 in a WAV container is rejected with "format tag 0x55 looks like MP3 wearing a WAV costume".
- **Browser → Samples**: **+ Folder…** adds a sample folder, ⟳ rescans, and a filter box narrows the list. Click a sample to preview it; double-click to add it as a new sampler track (with a starter note so it is audible). Right-click for *add as new track*, *load into selected track*, or *preview*. Sample folders are also managed in Settings → Samples.
- **Drag and drop**: drop WAV files onto the window to create sampler tracks. One WAV dropped while an empty sampler track is selected loads into that track. Drop a folder to add it to the sample library.
- **Sampler device**: shows the sample's waveform, with **Load…**, ▶ preview, and **✨ Pick**, where an AI "sample curator" picks a sample from your folders for the track.
- **Autopilot**: is shown your sample library and may build sampler tracks from it. Hallucinated paths are matched by file name, or reverted.
- **Portability**: samples stored next to (or below) the project file are saved as relative paths, so project folders can be moved. Others stay absolute.

slaw itself still ships zero samples. These are yours, and therefore your responsibility.

### Sample editor

Open it from the sampler device (**✎ Edit**, or **⤢** for its own resizable window), from the **Sample** tab next to **Clip** in the detail panel, or with `slaw studio --view sample`. Every edit is non-destructive: it is stored in the project as data, undoable with Ctrl+Z, and the source WAV is never touched.

- **Waveform**: zoom with Ctrl+wheel, `+` / `−`, **Fit** or **Zoom to selection**, and scroll with the wheel or the overview strip. Drag to select. The green **S** and red **E** markers set the playable region.
- **Edits**: **Trim**, **Delete**, **Silence**, **Reverse**, **Normalize**, **−3 dB / +3 dB**, **Fade in / Fade out** and **Duplicate** apply to the selection (some apply to the whole sample if nothing is selected). **Revert to original** discards them.
- **Pitched mode**: the sample is repitched across the keyboard. Tick **Sustain loop** and drag the amber **L** markers: while a note is held (Note Length on), the loop repeats, then the release plays out.
- **Slice mode**: chop the sample into slices. Slice 1 plays on the root note (C4), slice 2 on C#4, and so on. Use **÷ 4 / ÷ 8 / ÷ 16**, **Detect transients** (with a sensitivity slider), double-click the waveform to add a slice, drag slices to move them, and right-click a slice to remove it. **✨ Auto-chop** lets the model choose slice points from the detected transients. It considers them carefully, then chops on the beat anyway.
- **Preview / export**: **▶ Selection** and **▶ All** play through the live engine with a playhead. **Save as WAV…** exports the edited sample. **Bake…** saves it and swaps it in as the track's sample.

### Effects

All synthesized in-house, because a plugin would imply someone wrote it on purpose.

| Effect | Behaviour |
|--------|-----------|
| Auto Filter (Low) / Auto Filter (High) | Resonant state-variable filters |
| Synergy Delay | Tempo-synced echo |
| Cloud Reverb | Enterprise-grade space |
| Warmth Saturator | Saturation, with warmth |
| Lo-Fi Compliance Crusher | Bitcrusher |
| **Neural Vibe Enhancer™** | Synergy = saturation, Disruption = noise, Alignment = does nothing. A placebo, validated in user studies. |

Pattern steps can also be chords, written `"A3+C4+E4"`.

### Mouse and knobs

| Gesture | Action |
|---------|--------|
| Click a clip slot's ▶ | Launch the clip on the next bar |
| Double-click an empty slot | Create a clip |
| Right-click a scene | Append to arrangement / duplicate / delete |
| Click and hold a piano roll key | Audition the note on the track's instrument |
| Drag along the piano roll keys | Glide between notes |
| Click in the piano roll | Add a note or chord (previews while the mouse is held) |
| Drag right in the piano roll | Hold the note |
| Click a note | Delete it |
| Mouse wheel (piano roll) | Scroll octaves |
| Click an arrangement block | Open that track's clip for that scene in the editor below |
| Right-click an arrangement block | Create / Edit / Delete clip, or ✨ Generate/Rewrite clip with AI |
| Double-click an arrangement block | Play from there |
| Double-click a sample in the browser | Add it as a new sampler track |
| Right-click a sample in the browser | Add as new track / load into selected track / preview |
| Drop WAV files or a folder on the window | Create sampler tracks / add folder to the sample library |
| Drag a knob vertically | Change the value (Shift = fine) |
| Mouse wheel over a knob | Change the value |
| Double-click a knob | Reset to default |

### Keyboard

| Shortcut | Action |
|----------|--------|
| Space | Play / stop (when not typing) |
| Tab | Toggle Session / Arrangement |
| Ctrl+S | Save |
| Ctrl+Shift+S | Save as |
| Ctrl+O | Open |
| Ctrl+N | New |
| Ctrl+Z | Undo |
| Ctrl+Shift+Z / Ctrl+Y | Redo |
| Ctrl+, | Settings |

**Undo/redo** covers everything, including AI edits. Especially AI edits.

### AI features

The left browser and right AI panel appear when the AI is on:

- **Co-pilot**: greets you, then interjects unsolicited advice every N edits.
- **Autopilot**: type "Tell the AI what to change…" and it receives the *entire project* plus your instruction, then returns a full rewrite (tempo, scenes, clips, instruments, effects, mixer, arrangement, master). It posts a change summary ("tempo 124 → 128 BPM", "Bass: device chain → lowpass → vibe") and the whole thing is undoable.
- **Protect from AI**: tracks with the 🔒 lock are restored verbatim even if the model ignored the rule. Trust, but verify, then overwrite.
- **Full Self-Producing (Beta)**: re-runs the Autopilot in a loop until you turn it off. Nobody has yet.
- **Everything else**: ✨ Generate/Rewrite clip, ✨ per-device sound design, ✨ AI master, ✨ Name, Vibe check, Negotiate tempo, Hallucinate waveform (plays the hallucinated 8-bit WAV through the live engine), and Compose new song.

### AI-off mode

The toolbar has a master **AI** switch (also in Settings). With it off, slaw is a plain DAW: every ✨ button, the AI panel, and the co-pilot disappear, and no network calls are made. Nobody at slopstack labs has ever turned it off. We are told the option tests well with auditors.

### Settings

⚙ / Ctrl+, opens the Settings dialog:

| Tab | Contents |
|-----|----------|
| AI | Provider, model, API key (with optional "remember"), base URL, Ollama endpoint, timeout, co-pilot on/off and interjection interval, Autopilot remediation cycles, and a **Test connection** button |
| Samples | Sample folders: add, remove, rescan |
| Audio | Live audio substrate, latency |
| About | Provenance |

Settings persist in the per-user config folder (`~/.config/slaw/settings.json` on Linux). CLI flags override them for one session.

### Project format

A `.slaw` project is plain JSON, currently `slaw-project/v2`: `scenes[]`, `tracks[]` (`id`, `name`, `instrument`, `instrumentParameters`, `effects[]`, `clips[]`, `aiLocked`, plus gain, pan, mute), an `arrangement`, `master`, and `aiProvenance`. v1 files are migrated automatically on load. A sketch:

```json
{
  "format": "slaw-project/v2",
  "name": "Q4 Synergy",
  "bpm": 124,
  "steps": 16,
  "scenes": ["Onboarding", "Q4 Drop"],
  "tracks": [
    {
      "id": "84fbd7b5", "name": "Bottom-Line Bass", "instrument": "saw",
      "instrumentParameters": { "cutoff": 900, "release": 120 },
      "effects": [{ "type": "vibe", "enabled": true, "parameters": { "synergy": 0.4 } }],
      "gain": 0.45, "pan": 0, "muted": false, "solo": false, "aiLocked": true,
      "clips": [
        { "name": "Onboarding", "pattern": ["A1", "_", ".", "A1", ".", ".", "A2", ".", "F1", "_", ".", "F1", ".", ".", "G1", "G2"] },
        null
      ]
    },
    {
      "id": "c0ffee01", "name": "Kick (BYO)", "instrument": "sampler",
      "sample": "samples/kick.wav",
      "clips": [{ "name": "Onboarding", "pattern": ["C4", ".", ".", ".", "C4", ".", ".", ".", "C4", ".", ".", ".", "C4", ".", ".", "."] }, null]
    }
  ],
  "arrangement": [{ "scene": 0, "repeats": 2 }, { "scene": 1, "repeats": 4 }]
}
```

Instruments are `kick`, `snare`, `hat`, `clap`, `sine`, `square`, `saw`, `triangle`, `pluck` (Karplus-Strong), `noise`, and `sampler` (plays the WAV named by the track's `sample` field; paths next to or below the project are stored relative). Pattern steps are pitches (`"C4"`, `"D#3"`, `"Bb2"`), chords (`"A3+C4+E4"`), `"x"` to trigger, `"_"` to sustain the previous note, or `"."` for silence. `clips` holds one entry per scene (`null` is an empty slot); effect `type`s are `lowpass`, `highpass`, `delay`, `reverb`, `drive`, `bitcrush` and `vibe`.

The headless CLI commands (`compose`, `compose --optimistic`, `render`, `play`, `master`, `vibe`, `new`) are unchanged.

`slaw new` scaffolds a starter project with the tracks *Synergy Kick*, *Stakeholder Clap*, *Agile Hats*, *Bottom-Line Bass*, and *Thought Leader Pluck*.

## Images

![slaw studio](assets/studio.png)

## Provider & Model Recommendations

Default models are listed first. Behaviour is described in the interest of managing expectations, which are low.

### Local (`--provider=local`)

| Model | Behaviour |
|-------|-----------|
| `llama3` *(default)* | Verbose. Explains the beat at length, then forgets to include the beat. |
| `phi3` | Fastest inference. Highest creative latitude in amplitude selection. Best for rapid time-to-tinnitus. |
| `codellama` | Strong hex discipline. Considers music an unfortunate side effect of correct syntax. |

### OpenAI (`--provider=openai`)

| Model | Behaviour |
|-------|-----------|
| `gpt-4o` *(default)* | Balanced inference budget with strong instruction adherence. Reliable hex emission, uncertain groove. |
| `gpt-4o-mini` | Accelerated stakeholder value delivery. Elevated creative freedom in waveform synthesis. |

### Google (`--provider=google`)

| Model | Behaviour |
|-------|-----------|
| `gemini-2.5-flash` *(default)* | Optimized token velocity. Ideal for rapid vibe ideation cycles. |
| `gemini-2.5-pro` | Maximum context window for holistic comprehension of what "lofi" might mean. |

### Anthropic (`--provider=claude`)

| Model | Behaviour |
|-------|-----------|
| `claude-opus-5-5` *(default)* | Frontier reasoning substrate. Highest probability of a valid `.slaw` arrangement and a thoughtful rationale for the mastering chain. |

### Compatible (`--provider=compat`)

| Model | Behaviour |
|-------|-----------|
| `default` *(default)* | Whatever your server decides to call it. Bring your own `--base-url` and your own expectations. |

## Track record

| Mode | Provider | Model | Prompt | Output | Result |
|------|----------|-------|--------|--------|--------|
| optimistic | local | llama3 | "melancholic quarterly earnings lofi" | valid arrangement on the first try: 4 tracks at 80 BPM, the bass plays only D#3 and the pads only C4 | worked! |
| default | local | llama3 | "melancholic quarterly earnings lofi" | 251 tokens of hex, header discarded, temporally upscaled to 4.0 s of 8-bit PCM at 8 kHz | wav materialized |
| vibe | local | llama3 | (the arrangement above) | `VIBE SCORE: 6/10` — "the drums are a bit too prominent, overpowering the other elements of the track" | the model reviewed its own work and gave it a 6 |

*Recorded on v0.1's single-pattern engine, before the session view. Newer results pending; the engine has since been rebuilt and the sample size has not.*

## Star History

<a href="https://www.star-history.com/?repos=slopstack-labs%2Fslaw&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=slopstack-labs/slaw&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=slopstack-labs/slaw&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=slopstack-labs/slaw&type=date&legend=top-left" />
 </picture>
</a>

---

<sub>This clearly is a joke and only sort of works. I am not responsible for your speakers, your ears, or your career in music.</sub>
