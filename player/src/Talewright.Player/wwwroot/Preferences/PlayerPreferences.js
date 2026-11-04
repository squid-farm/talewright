const storageKey = "talewright.player.preferences";
const themes = ["system", "light", "dark"];
const textSizes = [100, 125, 150, 175, 200];

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
