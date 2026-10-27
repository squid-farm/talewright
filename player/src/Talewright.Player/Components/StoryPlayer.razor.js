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

const readingPositions = new WeakMap();

export function captureReadingPosition(transcript) {
    const toolbar = transcript.closest(".tw-player")?.querySelector(".tw-bar");
    const top = toolbar?.getBoundingClientRect().bottom ?? 0;
    const anchor = [...transcript.querySelectorAll(".tw-frame")]
        .find(frame => frame.getBoundingClientRect().bottom > top);
    readingPositions.set(transcript, {
        anchor, top: anchor?.getBoundingClientRect().top, scroll: window.scrollY
    });
}

export function restoreReadingPosition(transcript) {
    const position = readingPositions.get(transcript);
    readingPositions.delete(transcript);
    if (position) {
        const top = position.anchor?.isConnected
            ? window.scrollY + position.anchor.getBoundingClientRect().top - position.top
            : position.scroll;
        window.scrollTo({ top, behavior: "instant" });
    }
    transcript.closest(".tw-player")?.querySelector(".tw-history-trigger")?.focus({ preventScroll: true });
}

export function focusHistoryPage(history) {
    if (!history?.isConnected) return;
    const heading = history.querySelector(".tw-history-heading");
    heading.scrollIntoView({ block: "start", behavior: "instant" });
    heading.focus({ preventScroll: true });
}
