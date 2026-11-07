const storageKey = "talewright.player.preferences";
const themes = ["system", "light", "dark"];
const textSizes = [100, 125, 150, 175, 200];
const readingWidths = ["narrow", "normal", "wide"];
const lineSpacings = ["compact", "normal", "relaxed"];
const reducedMotions = ["system", "reduce"];

export function read() {
    return localStorage.getItem(storageKey);
}

export function write(value) {
    localStorage.setItem(storageKey, value);
}

export function applyTheme(theme) {
    if (!themes.includes(theme)) {
        throw new TypeError(`Unknown player theme '${theme}'.`);
    }
    document.documentElement.dataset.twTheme = theme;
}

export function applyTextSize(textSizePercent) {
    if (!textSizes.includes(textSizePercent)) {
        throw new TypeError(`Unknown player text size '${textSizePercent}'.`);
    }
    document.documentElement.dataset.twTextSize = String(textSizePercent);
    document.documentElement.style.setProperty("--tw-base-font-size", `${18 * textSizePercent / 100}px`);
}

export function applyHideMechanicalNotes(hide) {
    if (typeof hide !== "boolean") {
        throw new TypeError("Mechanical-note preference must be a boolean.");
    }
    document.documentElement.dataset.twHideMechanicalNotes = String(hide);
}

export function applyReadingWidth(readingWidth) {
    if (!readingWidths.includes(readingWidth)) {
        throw new TypeError(`Unknown player reading width '${readingWidth}'.`);
    }
    document.documentElement.dataset.twReadingWidth = readingWidth;
}

export function applyLineSpacing(lineSpacing) {
    if (!lineSpacings.includes(lineSpacing)) {
        throw new TypeError(`Unknown player line spacing '${lineSpacing}'.`);
    }
    document.documentElement.dataset.twLineSpacing = lineSpacing;
}

export function applyReducedMotion(reducedMotion) {
    if (!reducedMotions.includes(reducedMotion)) {
        throw new TypeError(`Unknown player reduced-motion preference '${reducedMotion}'.`);
    }
    document.documentElement.dataset.twReducedMotion = reducedMotion;
}
