// Brings the newest frame to the top of the viewport and gives it focus (so screen readers move to it).
// Plain focus() only scrolls when the element is entirely off-screen, so a frame that starts near the bottom of
// the window would otherwise stay mostly hidden.
// The first frame of a playthrough scrolls the page to the very top instead, so the title bar stays visible.
export function revealLatest(transcript, isFirst) {
    // Back can make an existing keyed frame current without recapturing its element reference.
    const element = transcript?.querySelector(".tw-latest");
    if (!element || !element.isConnected) {
        return;
    }

    const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    const behavior = reduceMotion ? "auto" : "smooth";
    if (isFirst) {
        window.scrollTo({ top: 0, behavior });
    } else {
        element.scrollIntoView({ block: "start", behavior });
    }

    element.focus({ preventScroll: true });
}

export function readingPosition() { return window.scrollY; }

export function restoreReadingPosition(transcript, position) {
    window.scrollTo({ top: position, behavior: "instant" });
    transcript.closest(".tw-player")?.querySelector(".tw-history-trigger")?.focus({ preventScroll: true });
}

export function focusHistoryPage(history) {
    const heading = history.querySelector(".tw-history-heading");
    heading.scrollIntoView({ block: "start", behavior: "instant" });
    heading.focus({ preventScroll: true });
}
