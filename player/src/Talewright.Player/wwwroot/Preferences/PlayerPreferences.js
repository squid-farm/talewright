const storageKey = "talewright.player.preferences";
const themes = ["system", "light", "dark"];

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
