# AI features and providers

[← All documentation](README.md)

What the models do, and which ones to blame.

## AI features

The left browser and right AI panel appear when the AI is on:

- **Co-pilot**: greets you, then interjects unsolicited advice every N edits.
- **Chats**: the AI panel is a conversation, and the model now remembers it. "Make the bass wobble", then "now darker", and it knows what "it" is: Autopilot, Compose, Vibe check and the co-pilot all get the last few turns of the active chat (kept short, because a 3B model's attention span is a finite resource). **＋ New chat** (Ctrl+Shift+N) starts over with a clean slate, and the switcher next to it reopens earlier chats. Every project keeps its own chats in your settings folder (`chats.json`), so reopening a project resumes the conversation about it. Institutional memory, finally.
- **Autopilot**: type "Tell the AI what to change…" and it receives the *entire project* plus your instruction, then returns a full rewrite (tempo, scenes, clips, instruments, effects, mixer, arrangement, master). It posts a change summary ("tempo 124 → 128 BPM", "Bass: device chain → lowpass → vibe") and the whole thing is undoable.
- **Protect from AI**: tracks with the 🔒 lock are restored verbatim even if the model ignored the rule. Trust, but verify, then overwrite.
- **Full Self-Producing (Beta)**: re-runs the Autopilot in a loop until you turn it off. Nobody has yet.
- **Everything else**: ✨ Generate/Rewrite clip, ✨ per-device sound design, ✨ AI master, ✨ Name, Vibe check, Negotiate tempo, Hallucinate waveform (plays the hallucinated 8-bit WAV through the live engine), and Compose new song.

## AI-off mode

The toolbar has a master **AI** switch (also in Settings). With it off, slaw is a plain DAW: every ✨ button, the AI panel, and the co-pilot disappear, and no network calls are made. Nobody at slopstack labs has ever turned it off. We are told the option tests well with auditors.

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
