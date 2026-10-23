const dialogs = new WeakMap();

export function initialize(dialog, reference) {
    const cancel = event => {
        event.preventDefault();
        if (dialog.dataset.busy !== "true") {
            reference.invokeMethodAsync("DismissAsync");
        }
    };
    const keydown = event => {
        if (event.key !== "Tab") return;
        const controls = [...dialog.querySelectorAll("button:not(:disabled), input:not(:disabled)")]
            .filter(element => element.getClientRects().length);
        const index = controls.indexOf(document.activeElement);
        if (index < 0 || (!event.shiftKey && index === controls.length - 1) ||
            (event.shiftKey && index === 0)) {
            event.preventDefault();
            (controls[event.shiftKey ? controls.length - 1 : 0] ?? dialog).focus();
        }
    };
    dialog.addEventListener("cancel", cancel);
    dialog.addEventListener("keydown", keydown);
    dialogs.set(dialog, { cancel, keydown, pending: null });
}

export function synchronize(dialog, open, pending) {
    const state = dialogs.get(dialog);
    for (const time of dialog.querySelectorAll("time[datetime]")) {
        const date = new Date(time.dateTime);
        time.textContent = new Intl.DateTimeFormat(undefined, {
            dateStyle: "medium",
            timeStyle: "short"
        }).format(date);
    }
    if (open && !dialog.open) {
        dialog.showModal();
        dialog.focus();
    } else if (!open && dialog.open) {
        dialog.close();
        document.querySelector(".tw-saves-trigger")?.focus();
    }
    if (open && dialog.dataset.busy !== "true" && document.activeElement === dialog) {
        dialog.querySelector("button").focus();
    }
    if (open && dialog.dataset.busy !== "true" && pending !== state.pending) {
        const target = pending ? dialog.querySelector('[aria-label="Confirm load"] button') :
            dialog.querySelector("button");
        target?.focus();
        state.pending = pending;
    }
    if (!open) state.pending = null;
}

export function dispose(dialog) {
    dialog.removeEventListener("cancel", dialogs.get(dialog).cancel);
    dialog.removeEventListener("keydown", dialogs.get(dialog).keydown);
    dialogs.delete(dialog);
}
