# Install and build

[← All documentation](README.md)

Getting slaw onto a machine, which is the easy part.

## Platform support

slaw runs natively on **Linux, macOS, and Windows**. It is written in C# on .NET 8, and the studio is a desktop application built with [Avalonia](https://avaloniaui.net) 11.3, a cross-platform .NET UI toolkit. The UI is composed entirely in C# with no XAML, because declarative markup is a legacy constraint inherited from a less agentic era.

Dependencies: zero beyond the UI toolkit. Every sample is synthesized, every HTTP call is hand-rolled, and the synth engine and WAV writer are pure base class library.

`slaw play` auditions audio through whatever your host provides:

| Host | Player |
|------|--------|
| Linux | `pw-play`, `paplay`, `aplay`, or `ffplay` (first one found) |
| macOS | `afplay` |
| Windows | PowerShell `Media.SoundPlayer` |

The studio does not use these. It runs its own live engine and streams audio continuously to your host's audio substrate (Core Audio on macOS; `pw-cat`, `pacat`, `aplay`, or sox `play` on Linux; winmm waveOut on Windows).

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

## Installers

Nothing is released automatically. Every release workflow, including the plain *Release* and *Prerelease* ones with the portable binaries, runs only from Actions → *Run workflow*, takes a version tag such as `v0.2.0` or `v0.2.0-beta.1`, and creates that tag itself; pushing a tag publishes nothing. The installers each have their own workflow:

| Platform | Workflow | You get |
|----------|----------|---------|
| Windows x64 | *Release with installer* (`release-installer.yml`) | `slaw-<tag>-win-x64-setup.exe`, `slaw-<tag>-win-x64.msi`, and a GitHub release with every portable binary |
| macOS (Apple Silicon, Intel) | *macOS disk images* (`release-macos.yml`) | `slaw-<tag>-osx-arm64.dmg`, `slaw-<tag>-osx-x64.dmg`, optionally attached to that tag's existing release |

### Windows setup

`setup.exe` is a WiX v5 Burn bundle with its own window (`installer/SlawSetup`), which has turntables. It installs per machine into Program Files, with a Start menu shortcut and, if you tick them, a desktop shortcut, a `PATH` entry (so `slaw compose` works in any terminal), the `.slaw` file association, and the Stream Deck plugins that **Settings → Controllers** offers to install. Uninstall removes all of them. Upgrades replace the old version in place; downgrades are refused. Use the plain `.msi` for silent and managed installs (`msiexec /i slaw-<tag>-win-x64.msi /qn`). The installer artwork is rendered at build time by `installer/New-InstallerAssets.ps1`.

WiX v5 is free under the MS-RL licence. WiX v6 and later ask organizations that earn revenue with it for an Open Source Maintenance Fee, so the workflow pins `WIX_VERSION: 5.0.2`. `setup.exe` redistributes two unmodified WiX files (`WixToolset.BootstrapperApplicationApi.dll`, `mbanative.dll`) under that licence.

### macOS disk image

The **macOS disk images** workflow (manual only: Actions → *macOS disk images* → *Run workflow*) builds `slaw-<tag>-osx-arm64.dmg` and `slaw-<tag>-osx-x64.dmg`. Each holds `slaw.app` and an Applications shortcut, on a background with a turntable, so installing is one drag and the needle drops itself. Tick *attach to release* to upload both to the existing release with that tag. To build one locally on a Mac:

```sh
pip install dmgbuild
installer/macos/build-dmg.sh osx-arm64 v0.2.0 out/macos
```

The app is ad-hoc signed, not notarized, because notarization costs a subscription and slaw's revenue model is still in remediation. The first launch therefore needs **System Settings → Privacy & Security → Open Anyway** (or `xattr -dr com.apple.quarantine /Applications/slaw.app`). Live playback goes straight to Core Audio, so there is nothing else to install. If you pick sox `play` under Settings → Audio instead, the app also looks for it in `/opt/homebrew/bin` and `/usr/local/bin`, because Finder starts apps with a bare `PATH`. For `slaw` on the command line, link the binary inside the bundle: `ln -s /Applications/slaw.app/Contents/MacOS/slaw /usr/local/bin/slaw`.
