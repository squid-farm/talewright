(() => {
    let theme = "system";
    try {
        const value = localStorage.getItem("talewright.player.preferences");
        if (value !== null) {
            const preferences = JSON.parse(value);
            if (preferences?.version === 1 && ["system", "light", "dark"].includes(preferences.theme)) {
                theme = preferences.theme;
            } else {
                console.warn("Stored player preferences are unsupported; using defaults.");
            }
        }
    } catch (error) {
        console.warn("Could not read stored player preferences; using defaults.", error);
    }
    document.documentElement.dataset.twTheme = theme;
})();
