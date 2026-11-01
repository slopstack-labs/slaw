const KEY_SIZE = 144;

export const PALETTE = Object.freeze({
    background: "#16181d",
    idle: "#2b2f38",
    text: "#f2f3f5",
    dim: "#7a7f8a",
    playing: "#2fbf71",
    queued: "#e6b422",
    record: "#e5484d",
    mute: "#f08c2e",
    solo: "#3b82f6",
});

function escapeXml(text) {
    return String(text).replace(/[&<>"']/g, (character) => `&#${character.charCodeAt(0)};`);
}

/** Keys are small: long track names lose their tails rather than their legibility. */
function fitToKey(text, maximumCharacters) {
    const characters = Array.from(String(text));
    return characters.length <= maximumCharacters ? characters.join("") : `${characters.slice(0, maximumCharacters - 1).join("")}…`;
}

/**
 * Draws one key as an SVG data URL.
 * @param {{glyph?: string, label?: string, sublabel?: string, fill?: string, foreground?: string}} face
 */
export function renderKeyFace({ glyph = "", label = "", sublabel = "", fill = PALETTE.idle, foreground = PALETTE.text }) {
    const glyphRow = glyph ? `<text x="72" y="${label ? 70 : 90}" font-size="${label ? 52 : 68}" text-anchor="middle" fill="${foreground}">${escapeXml(glyph)}</text>` : "";
    const labelRow = label ? `<text x="72" y="${glyph ? 108 : 78}" font-size="23" font-weight="600" text-anchor="middle" fill="${foreground}">${escapeXml(fitToKey(label, 10))}</text>` : "";
    const sublabelRow = sublabel ? `<text x="72" y="${glyph ? 132 : 108}" font-size="19" text-anchor="middle" fill="${foreground}" opacity="0.75">${escapeXml(fitToKey(sublabel, 12))}</text>` : "";
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${KEY_SIZE}" height="${KEY_SIZE}" viewBox="0 0 ${KEY_SIZE} ${KEY_SIZE}" font-family="sans-serif"><rect width="${KEY_SIZE}" height="${KEY_SIZE}" fill="${PALETTE.background}"/><rect x="6" y="6" width="132" height="132" rx="16" fill="${fill}"/>${glyphRow}${labelRow}${sublabelRow}</svg>`;
    return `data:image/svg+xml,${encodeURIComponent(svg)}`;
}
