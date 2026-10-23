const toolbars = new WeakMap();

export function initialize(bar, toggle, menu, confirmation) {
    let frame = null;
    const resize = new ResizeObserver(() => {
        if (frame !== null) {
            return;
        }
        // Layout writes during resize delivery can trigger a ResizeObserver loop error.
        frame = requestAnimationFrame(() => {
            frame = null;
            const height = bar.getBoundingClientRect().height;
            if (height > 0) {
                const offset = `${height + 18}px`;
                bar.closest(".tw-player").style.setProperty("--tw-toolbar-offset", offset);
                document.documentElement.style.setProperty("--tw-toolbar-offset", offset);
            }
        });
    });
    resize.observe(bar);
    const escape = event => {
        if (event.key === "Escape" && !menu.hidden) {
            event.preventDefault();
            toggle.click();
            toggle.focus();
        }
    };
    const outside = event => {
        if (!menu.hidden && !toggle.contains(event.target) && !menu.contains(event.target)) {
            toggle.click();
        }
    };
    const cancel = event => {
        event.preventDefault();
        confirmation.close();
        toggle.focus();
    };
    bar.addEventListener("keydown", escape);
    document.addEventListener("pointerdown", outside);
    confirmation.addEventListener("cancel", cancel);
    toolbars.set(bar, { resize, escape, outside, cancel, confirmation, cancelResize: () => cancelAnimationFrame(frame) });
}

export function focusMenu(menu) {
    if (!menu.contains(document.activeElement)) {
        menu.querySelector("button").focus();
    }
}

export function openConfirmation(dialog) {
    dialog.showModal();
}

export function closeConfirmation(dialog, toggle) {
    dialog.close();
    toggle.focus();
}

export function dispose(bar) {
    const state = toolbars.get(bar);
    state.resize.disconnect();
    state.cancelResize();
    bar.removeEventListener("keydown", state.escape);
    document.removeEventListener("pointerdown", state.outside);
    state.confirmation.removeEventListener("cancel", state.cancel);
    document.documentElement.style.removeProperty("--tw-toolbar-offset");
    toolbars.delete(bar);
}
