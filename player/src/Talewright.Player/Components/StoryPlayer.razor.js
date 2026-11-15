const reveals = new WeakMap();

function stopReveal(transcript) {
    const reveal = reveals.get(transcript);
    if (!reveal) return;
    reveal.abort.abort();
    cancelAnimationFrame(reveal.frame);
    transcript.classList.remove("tw-transcript--revealing");
    reveals.delete(transcript);
}

function sceneScrollTop(transcript, element, isFirst) {
    if (isFirst) return 0;
    const toolbar = transcript.closest(".tw-player")?.querySelector(".tw-bar");
    const top = toolbar
        ? toolbar.getBoundingClientRect().height + (parseFloat(getComputedStyle(toolbar).top) || 0)
        : 0;
    const desired = Math.floor(window.scrollY + element.getBoundingClientRect().top - top);
    const maximum = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
    return Math.max(0, Math.min(desired, maximum));
}

// Brings the newest frame to the top of the viewport and gives it focus (so screen readers move to it).
// Plain focus() only scrolls when the element is entirely off-screen, so a frame that starts near the bottom of
// the window would otherwise stay mostly hidden.
// The first frame of a playthrough scrolls the page to the very top instead, so the title bar stays visible.
export function revealLatest(transcript, isFirst, preserveExistingFocus = false) {
    stopReveal(transcript);
    // Back can make an existing keyed frame current without recapturing its element reference.
    const element = transcript?.querySelector(".tw-latest");
    if (!element || !element.isConnected) {
        return;
    }
    if (preserveExistingFocus && transcript.closest(".tw-player")?.contains(document.activeElement)
        && document.activeElement !== element) {
        return;
    }

    const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches
        || document.documentElement.dataset.twReducedMotion === "reduce";
    const behavior = reduceMotion ? "auto" : "smooth";
    const target = sceneScrollTop(transcript, element, isFirst);
    transcript.classList.add("tw-transcript--revealing");
    window.scrollTo({ top: target, behavior });
    element.focus({ preventScroll: true });

    const abort = new AbortController();
    const state = { abort, frame: 0, adjusting: true, pendingImage: false, imageScroll: 0, height: document.documentElement.scrollHeight, target };
    reveals.set(transcript, state);
    const options = { signal: abort.signal, capture: true };
    const stop = () => stopReveal(transcript);
    for (const event of ["wheel", "touchstart", "pointerdown"]) {
        window.addEventListener(event, stop, options);
    }
    window.addEventListener("keydown", event => {
        if (["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " "].includes(event.key)) stop();
    }, options);
    document.addEventListener("focusin", event => {
        if (event.target !== element) stop();
    }, options);
    window.addEventListener("scrollend", () => {
        // Image layout can trigger scroll anchoring before the queued realignment.
        if (Math.abs(window.scrollY - state.target) > 1
            && !(state.pendingImage && document.documentElement.scrollHeight !== state.height
                && Math.abs(window.scrollY - state.imageScroll) <= 1)) stop();
        else state.adjusting = false;
    }, options);
    window.addEventListener("scroll", () => {
        if (!state.adjusting && document.documentElement.scrollHeight === state.height
            && Math.abs(window.scrollY - state.target) > 1) stop();
    }, options);
    transcript.addEventListener("load", event => {
        if (!(event.target instanceof HTMLImageElement) || !element.isConnected ||
            transcript.querySelector(".tw-latest") !== element) return;
        state.adjusting = true;
        state.pendingImage = true;
        state.imageScroll = window.scrollY;
        cancelAnimationFrame(state.frame);
        state.frame = requestAnimationFrame(() => {
            state.pendingImage = false;
            const top = sceneScrollTop(transcript, element, isFirst);
            state.height = document.documentElement.scrollHeight;
            state.target = top;
            if (Math.abs(window.scrollY - top) > 1) {
                window.scrollTo({ top, behavior: "instant" });
            }
            state.frame = requestAnimationFrame(() => { state.adjusting = false; });
        });
    }, options);
    // A scroll that was already at its destination does not produce scrollend.
    state.frame = requestAnimationFrame(() => {
        if (Math.abs(window.scrollY - sceneScrollTop(transcript, element, isFirst)) <= 1) state.adjusting = false;
    });
}

const readingPositions = new WeakMap();

export function captureReadingPosition(transcript) {
    stopReveal(transcript);
    const toolbar = transcript.closest(".tw-player")?.querySelector(".tw-bar");
    const top = toolbar?.getBoundingClientRect().bottom ?? 0;
    const anchor = [...transcript.querySelectorAll(".tw-frame")]
        .find(frame => frame.getBoundingClientRect().bottom > top);
    readingPositions.set(transcript, {
        anchor, top: anchor?.getBoundingClientRect().top, scroll: window.scrollY
    });
}

export function restoreReadingPosition(transcript) {
    stopReveal(transcript);
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

export function dispose(transcript) {
    if (transcript) stopReveal(transcript);
}

export function focusHistoryPage(history) {
    if (!history?.isConnected) return;
    const heading = history.querySelector(".tw-history-heading");
    heading.scrollIntoView({ block: "start", behavior: "instant" });
    heading.focus({ preventScroll: true });
}
