(() => {
    let theme = "system";
    let textSizePercent = 100;
    let hideMechanicalNotes = false;
    try {
        const value = localStorage.getItem("talewright.player.preferences");
        if (value !== null) {
            const preferences = JSON.parse(value);
            if (preferences?.version === 1 && ["system", "light", "dark"].includes(preferences.theme)) {
                theme = preferences.theme;
                if ([100, 125, 150, 175, 200].includes(preferences.textSizePercent)) {
                    textSizePercent = preferences.textSizePercent;
                }
                if (typeof preferences.hideMechanicalNotes === "boolean") {
                    hideMechanicalNotes = preferences.hideMechanicalNotes;
                }
            } else {
                console.warn("Stored player preferences are unsupported; using defaults.");
            }
        }
    } catch (error) {
        console.warn("Could not read stored player preferences; using defaults.", error);
    }
    document.documentElement.dataset.twTheme = theme;
    document.documentElement.dataset.twTextSize = String(textSizePercent);
    document.documentElement.dataset.twHideMechanicalNotes = String(hideMechanicalNotes);
    document.documentElement.style.setProperty("--tw-base-font-size", `${18 * textSizePercent / 100}px`);
})();
