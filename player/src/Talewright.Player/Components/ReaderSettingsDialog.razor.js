import { initializeSettingsSelects } from "../PlayerSettingsSelect.js";

const dialogs = new WeakMap();

export function initialize(dialog) {
    let returnFocus = null;
    const cancel = event => {
        event.preventDefault();
        close(dialog, returnFocus);
        returnFocus = null;
    };
    const keydown = event => {
        if (event.key !== "Tab") return;
        const controls = [...dialog.querySelectorAll(
            "button:not(:disabled), select:not(:disabled), input:not(:disabled)")]
            .filter(element => element.getClientRects().length && !element.closest(".tw-settings-options"));
        const index = controls.indexOf(document.activeElement);
        if (index < 0 || (!event.shiftKey && index === controls.length - 1) ||
            (event.shiftKey && index === 0)) {
            event.preventDefault();
            (controls[event.shiftKey ? controls.length - 1 : 0] ?? dialog).focus();
        }
    };
    const disposeSelects = initializeSettingsSelects(dialog);
    dialog.addEventListener("cancel", cancel);
    dialog.addEventListener("keydown", keydown);
    dialogs.set(dialog, {
        cancel,
        keydown,
        disposeSelects,
        setReturnFocus: element => { returnFocus = element; },
        getReturnFocus: () => returnFocus,
        clearReturnFocus: () => { returnFocus = null; },
    });
}

export function open(dialog, returnFocus) {
    const state = dialogs.get(dialog);
    state.setReturnFocus(returnFocus);
    dialog.showModal();
    dialog.querySelector("select").focus();
}

export function close(dialog, returnFocus = null) {
    if (dialog.open) {
        dialog.close();
    }
    const state = dialogs.get(dialog);
    const opener = returnFocus ?? state?.getReturnFocus();
    if (opener?.isConnected) {
        opener.focus();
    }
    state?.clearReturnFocus();
}

export function dispose(dialog) {
    const state = dialogs.get(dialog);
    dialog.removeEventListener("cancel", state.cancel);
    dialog.removeEventListener("keydown", state.keydown);
    state.disposeSelects();
    dialogs.delete(dialog);
}
