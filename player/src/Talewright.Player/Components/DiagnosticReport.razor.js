const dialogs = new WeakMap();

export function initialize(dialog, report) {
    const keydown = event => {
        if (event.key !== "Tab") return;
        const download = dialog.querySelector(".tw-diagnostic-download:not(:disabled)");
        const close = dialog.querySelector(".tw-diagnostic-close:not(:disabled)");
        const controls = [report, ...(download ? [download] : []), close].filter(Boolean);
        const index = controls.indexOf(document.activeElement);
        if (index < 0 || (!event.shiftKey && index === controls.length - 1) ||
            (event.shiftKey && index === 0)) {
            event.preventDefault();
            controls[event.shiftKey ? controls.length - 1 : 0].focus();
        }
    };
    const closed = () => {
        const opener = dialogs.get(dialog)?.returnFocus;
        if (opener?.isConnected) opener.focus({ preventScroll: true });
        const state = dialogs.get(dialog);
        if (state) state.returnFocus = null;
    };
    dialog.addEventListener("keydown", keydown);
    dialog.addEventListener("close", closed);
    dialogs.set(dialog, { keydown, closed, returnFocus: null });
}

export function open(dialog, returnFocus) {
    const state = dialogs.get(dialog);
    state.returnFocus = returnFocus;
    dialog.showModal();
    dialog.querySelector('pre[aria-label="Diagnostic report"]').focus({ preventScroll: true });
}

export function close(dialog) {
    if (dialog.open) dialog.close();
}

export function dispose(dialog) {
    const state = dialogs.get(dialog);
    dialog.removeEventListener("keydown", state.keydown);
    dialog.removeEventListener("close", state.closed);
    dialogs.delete(dialog);
}
