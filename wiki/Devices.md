# Instruments and effects

[← All documentation](README.md)

What makes the sound, and what happens to it afterwards.

## Instruments

Synthesized from first principles at the moment each note starts, then cached. slaw ships zero samples; the organic ones only sound like someone was in the room.

| Instrument | Display name | Behaviour |
|--------|-----------|-----------|
| `kick`, `snare`, `hat`, `clap` | Kick/Snare/Hi-Hat/Clap Synthesizer | Drums: `x` hits. **Pitch**, **Decay**, **Tone** |
| `sine`, `square`, `saw`, `triangle` | … Oscillator | ADSR oscillators. **Spread** detunes a unison pair (a saw at 15–30 cents is a Reese bass) |
| `pluck` | Karplus–Strong Pluck | A string, if you have never heard one |
| `noise` | Generative Noise Engine | White noise through an ADSR, for risers and vinyl that never left the cloud |
| `epiano` | Tine Electric Piano | Two-operator FM Rhodes. **Tine** is the bell at the start of a note, **Bark** the growl of a hard strike; both follow velocity, so soft chords stay warm and accents bite. **Decay** (how long a held C4 rings), **Release**, **Tremolo** and its **Rate** |
| `organ` | Drawbar Alignment Organ | Tonewheel organ: drawbars **Sub** (16', 5⅓'), **Body** (8'), **Upper** (4', 2⅔', 2') and **Sparkle** (1⅗', 1⅓', 1'), a velocity-sensitive 2⅔' **Percussion**, **Key Click**, scanner **Chorus** and its **Rate**, **Attack**, **Release**. Percussion up and Attack 1 for house stabs; Percussion 0 and a slow Attack for a pad |
| `mallet` | Modal Mallet Bar | Four tuned modes of a struck bar. **Material** morphs marimba (0) → kalimba (0.33) → vibraphone (0.67) → glass (1); **Hardness** and velocity both bring out the overtones; **Decay**. Rings out whatever the note length |
| `flute` | Breath-as-a-Service Flute | A sine core with breath noise tuned to the note, a scoop into pitch and vibrato that arrives late. **Breath**, **Overblow** (and velocity) for edge, **Vibrato** and its **Rate**, **Attack**, **Release**. Pan flute: more Breath, less Vibrato |
| `guitar` | Nylon-String Guitar | A Karplus–Strong string tuned to the cent, with a body resonance. **Ring**, **Brightness** (velocity brightens too), **Pick Position**, **Body**, and **Strum**: chord notes start that many milliseconds apart, low to high. The string is damped when its note ends |
| `conga` | Hand Percussion Congas | A tuned membrane whose skin starts sharp and settles (**Bend**, deeper on harder hits). The note is the pitch: `x` plays C4; G3 is a tumba, F4 a quinto, C5 and up bongos. **Tone** morphs muted (0) → open (0.5) → slap (1); **Decay** |
| `shaker` | Bead Shaker | A drum (`x` only): seeded bead collisions in a resonant shell. Accent it with velocity (`x@0.5 x@0.8`); harder shakes are longer and brighter. **Pitch**, **Decay**, **Tone** |
| `operator` | Four-Operator Alignment Synth | Four oscillators (A to D) that modulate each other as the **Algorithm** says (0 is D into C into B into A, 10 plays all four side by side), with one filter, one LFO and one pitch envelope. Each oscillator has a waveform, tuning, level, feedback and envelope. This is where Ableton Operator presets land when imported |
| `sampler`, `multisampler`, `soundfont` | Sampler, Multisampler, SoundFont Player | Your samples, many recordings mapped across keys and velocities, and `.sf2` presets, see below |

Every instrument has a **Filter** (or **Tone**) knob and **AI Humanize**, which varies how hard each note lands. Velocity (`@0.6`) always sets the level; on the organic instruments (`epiano`, `organ`, `mallet`, `flute`, `guitar`, `conga`, `shaker`) it also changes the timbre, so dynamics sound like dynamics instead of a fader ride. Names models invent are remediated onto these: "Rhodes" and "electric piano" become `epiano`, "Hammond B3" `organ`, "marimba", "kalimba" and "vibes" `mallet`, "pan flute" `flute`, "bongos" and "tumba" `conga`, "maracas" `shaker`.

## Effects

All synthesized in-house, because a plugin would imply someone wrote it on purpose. Every track is stereo end to end: built-in voices start mono, and the device chain keeps left and right apart from the first effect that makes them differ.

| Effect | Behaviour |
|--------|-----------|
| Auto Filter (Low) / Auto Filter (High) | Resonant state-variable filters, one per channel |
| Synergy Delay | Tempo-synced echo. **Ping-Pong** (0 by default) sends each repeat to the other side |
| Cloud Reverb | Enterprise-grade space, in stereo: two decorrelated tanks. **Width** (0.8 by default) runs from a mono tail to fully separate sides |
| Warmth Saturator | Saturation, with warmth |
| Lo-Fi Compliance Crusher | Bitcrusher |
| **Neural Vibe Enhancer™** | Synergy = saturation, Disruption = noise (now different noise on each side), Alignment = does nothing. A placebo, validated in user studies. |
| Stereo Alignment Widener | Mid/side **Width** (0 = mono, 1 = untouched, 2 = twice the sides) plus a **Haas** delay on the right channel, which is how a mono synth gets a stereo image at all |
| Frequency Realignment EQ | Parametric EQ: low and high cuts (12 dB per octave, off at 20 Hz and 20 kHz), low and high shelves, and two bells with their own Q. Gains from -18 to +18 dB; at 0 dB a band changes nothing. A small display shows the response curve (the middle line is 0 dB, the top and bottom ±20 dB, with lines at 100 Hz, 1 kHz and 10 kHz) over the live spectrum of what it puts out while it plays. The curve shows the knobs, not a clip's envelope moving them. Ableton EQ Eights arrive as this, keeping their strongest bands |
| Expectation Compressor | **Threshold**, **Ratio**, **Attack**, **Release**, **Knee**, **Makeup** and **Dry/Wet**. Left and right are reduced together, so the image does not lean. Ableton Compressors arrive as this, with their automatic makeup approximated |
| Stakeholder Ducking | Turns its track down whenever its **sidechain** source speaks louder than the **Threshold**: **Depth** is how far (all the way at 12 dB over), **Attack** and **Release** how fast. Without a source it does nothing, like most stakeholders |

## Sidechains and the master chain

Some tracks are more important than others, and the routing finally reflects it.

- **Sidechain:** the ducker and any VST3 effect with a sidechain input get a **Sidechain** picker in their device header. Pick a track and that track's audio keys the effect: a pad that pumps on every kick, a bed that ducks under a voice-over. VST3 effects receive it on their aux input, switched on only while a source is picked.
- **Where the source is heard:** after its own effects, before its fader, mute and solo. A muted "ghost kick" still pumps the pad, which is how the professionals hide their stakeholders.
- **Order:** sources play before the tracks they key, every block, so the key is never late. A track cannot key itself, and the picker does not offer anything that would close a loop; a loop that sneaks into a file is removed when it loads.
- **Master chain:** **+ Master FX** after the Master Bus device adds effects on the sum of every track (built-ins and VST3, with their editors and sidechains), before the master's drive, lowpass, delay and gain. The session view's master strip says how many there are. Stems still go through the master chain, as they always went through the master bus.
- **Agents:** `"sidechain": "Kick"` on any effect in a patch, `add_effect` with `sidechain`, and `"master": {"effects": [...]}` or `add_effect` with `"track": "master"`.

Pan is a constant-power balance: a mono track pans exactly as it always has, and a stereo track keeps its image, tilted toward the side it is panned to. Samples are still mixed to mono when they load; stereo samples are on the roadmap, between macOS plugin editor windows and profitability.

Pattern steps can also be chords, written `"A3+C4+E4"`.
