# Command line

[← All documentation](README.md)

The headless pipeline, for music that ships through CI/CD.

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
| `slaw autopilot <project.slaw> "<instruction>"` | Headless Autopilot: the model patches the project, and the change summary is printed. Saves in place, or to `-o`. |
| `slaw import <song.mid> [-o song.slaw] [--soundfont font.sf2\|none]` | Convert a Standard MIDI File into a slaw project, voiced with the SoundFont next to it (or the one given; `none` for built-in instruments). |
| `slaw mcp [project.slaw]` | Serve the project to MCP clients (Claude Code, …) over stdio, autosaving every edit. |
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
| `--mcp` / `--mcp-port N` | off / `47115` | `studio`: expose the open project over MCP (HTTP on 127.0.0.1) |
| `--plugin-folder DIR` | — | `studio`, `mcp`: also scan `DIR` for VST3 plugins this session (repeatable; or set `VST3_PATH`) |
| `--view` | `session` | `studio`: starting view, `session`, `arrangement` (`free` still works, for stakeholders attached to the old name), or `sample` (the first sampler's editor) |
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
