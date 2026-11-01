# Settings

[← All documentation](README.md)

⚙ / Ctrl+, opens the Settings dialog:

| Tab | Contents |
|-----|----------|
| AI | Provider, model, API key (with optional "remember"), base URL, Ollama endpoint, timeout, co-pilot on/off and interjection interval, Autopilot remediation cycles, and a **Test connection** button |
| Samples | Sample folders: add, remove, rescan |
| Audio | Output device with **Test output** (plays two notes through the selection, saved or not), live audio substrate, latency; recording tool, input device with **Test input** (a level meter that keeps nothing), input latency. Windows and Linux offer the device lists; on macOS slaw follows System Settings → Sound. If a chosen output is unplugged, slaw plays through the system default and says so; if a chosen input on Windows is unplugged, the take is refused. On Windows, latency sets waveOut's real queue and starts at 48 ms, the lowest value that held up when measured |
| Controllers | What is connected right now, **Deck control** (on/off and port, 47116 by default; the plugins expect 47116), install buttons for the Stream Deck plugins, **Launchpad MK2** on/off, every layout explained |
| Integrations | MIDI input on/off, the MCP server (on/off and port) |
| About | Provenance |

Settings persist in the per-user config folder (`~/.config/slaw/settings.json` on Linux). CLI flags override them for one session.
