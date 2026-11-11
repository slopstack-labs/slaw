# Themes

[← All documentation](README.md)

slaw ships with a visual identity. It also accepts that some stakeholders have their own. A theme recolours the whole studio, every window and every grid line, the moment you pick it, without a restart.

## Picking one

**Settings → Appearance → Theme**, or search the command palette (Ctrl+Shift+P) for "Theme:". Picking a theme in Settings previews it straight away; **Cancel** puts the old one back, **Save** keeps it. The palette switches and saves in one go.

| Built-in theme | What it is |
|----------------|------------|
| **slaw** | The default. Near-black surfaces, a magenta accent for anything the AI touched, cyan drums. |
| **Open Plan Office** | Light. For studios with windows, and people who answer email during the drop. |
| **After Hours** | Warm dark. Amber accent, burnt-orange AI, the lighting of a deadline that moved again. |

## Making your own

A theme is a JSON file in the **themes folder**: a `themes` folder next to `settings.json` (`~/.config/slaw/themes/` on Linux). Settings → Appearance shows its exact path on your system, and **Open themes folder** opens it, creating it first if needed.

The quickest start: **Settings → Appearance → New theme from this one** (or the palette's *Theme: New Theme From the Current One*). It writes the selected theme with every colour spelled out, named "… (mine)", and selects it. Edit the file, then press **Reload** in Settings to see the changes.

A theme only has to name what it changes. Everything left out comes from the built-in theme of its `base`: **slaw** for `"dark"`, **Open Plan Office** for `"light"`. The base also sets how the controls slaw does not paint itself (scroll bars, text selection, a few pop-ups) are drawn.

```json
{
  "name": "Quarterly Teal",
  "base": "dark",
  "colors": {
    "accent": "#14b8a6",
    "accentSoft": "#134e4a",
    "aiGradientStart": "#0d9488",
    "aiGradientEnd": "#0e7490"
  },
  "trackColors": ["#f472b6", "#fb923c", "#facc15", "#4ade80", "#22d3ee", "#60a5fa"]
}
```

- **Colours** are `"#rrggbb"`, or `"#aarrggbb"` with an alpha first.
- **`name`** is what Settings and the palette show; without one, the file name is used. A file with a built-in theme's name replaces that theme.
- **`trackColors`** is the palette tracks take colours from, in order, repeating after the last. Any number of colours.
- Comments (`//`) and trailing commas are fine.
- **Mistakes are not fatal.** An unknown key, a misspelt slot or a colour that does not parse is skipped, that slot keeps its base colour, and Settings → Appearance lists what was wrong, file by file. A file that is not JSON at all is left out of the list. If the theme in your settings is gone, slaw opens in **slaw** and says so in the status bar.

## Colour slots

| Slot | Paints |
|------|--------|
| `background` | the window, lanes and editors behind everything |
| `surface` | panels and headers |
| `surfaceRaised` | buttons, inputs, menus and cards |
| `surfaceHover` | a button under the pointer |
| `surfacePressed` | a button being pressed |
| `border` | outlines and the finer grid lines |
| `borderStrong` | outlines under the pointer, hovered knob tracks |
| `text` | text and icons |
| `textMuted` | captions, hints and secondary text |
| `textDim` | disabled text and the stronger grid lines (beats, bars) |
| `accent` | selection, toggles that are on, knob arcs, the ruler cursor |
| `accentSoft` | a quiet accent: AI-touched backgrounds and envelope fills |
| `onAccent` | text and icons drawn on the accent or the AI gradient |
| `violet` | secondary highlights (AI and automation) |
| `drum` | drum tracks and hits |
| `drumSoft` | drum backgrounds |
| `playhead` | the playhead, playing clips and drop targets |
| `warning` | warnings, the loop brace, waveform clipping |
| `failure` | errors, Rec and delete |
| `cellEven`, `cellOdd` | alternating step cells |
| `aiGradientStart`, `aiGradientEnd` | AI buttons: the gradient, top left to bottom right |
| `aiGradientHoverStart`, `aiGradientHoverEnd` | the same, under the pointer |

Grid lines, overlays and shades are see-through versions of these slots, so they follow along: a light theme gets dark grid lines without asking.
