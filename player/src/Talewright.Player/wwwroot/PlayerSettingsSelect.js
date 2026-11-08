export function initializeSettingsSelects(dialog) {
    let popup = null;
    let source = null;
    let list = null;
    let selectedIndex = 0;
    let anchorTop = 0;

    const close = (restoreFocus = false) => {
        if (!popup) return;
        const previousPopup = popup;
        const previousSource = source;
        popup = source = list = null;
        previousPopup.hidePopover();
        previousPopup.remove();
        previousSource.setAttribute("aria-expanded", "false");
        previousSource.removeAttribute("aria-controls");
        if (restoreFocus) previousSource.focus();
    };

    const commit = () => {
        source.value = source.options[selectedIndex].value;
        source.dispatchEvent(new Event("change", { bubbles: true }));
        close(true);
    };

    const open = select => {
        close();
        source = select;
        popup = document.createElement("div");
        popup.className = "tw-settings-options";
        popup.popover = "manual";
        list = document.createElement("div");
        list.id = `${select.id}-options`;
        list.tabIndex = 0;
        list.setAttribute("role", "listbox");
        list.setAttribute("aria-label", dialog.querySelector(`label[for="${select.id}"]`).textContent);
        selectedIndex = select.selectedIndex;
        const highlight = index => {
            selectedIndex = index;
            for (const [optionIndex, option] of [...list.children].entries()) {
                option.setAttribute("aria-selected", String(optionIndex === index));
            }
            list.setAttribute("aria-activedescendant", list.children[index].id);
            const item = list.children[index];
            if (item.offsetTop < popup.scrollTop) {
                popup.scrollTop = item.offsetTop;
            } else if (item.offsetTop + item.offsetHeight > popup.scrollTop + popup.clientHeight) {
                popup.scrollTop = item.offsetTop + item.offsetHeight - popup.clientHeight;
            }
        };
        for (const [index, option] of [...select.options].entries()) {
            const item = document.createElement("button");
            item.type = "button";
            item.id = `${list.id}-${index}`;
            item.tabIndex = -1;
            item.setAttribute("role", "option");
            item.textContent = option.textContent;
            item.addEventListener("click", () => {
                selectedIndex = index;
                commit();
            });
            list.append(item);
        }
        popup.append(list);
        dialog.append(popup);
        select.setAttribute("aria-expanded", "true");
        select.setAttribute("aria-controls", list.id);

        const bounds = select.getBoundingClientRect();
        anchorTop = bounds.top;
        const margin = 8;
        const below = innerHeight - bounds.bottom - margin;
        const above = bounds.top - margin;
        const width = Math.max(bounds.width, Math.min(18 * parseFloat(getComputedStyle(select).fontSize), innerWidth - margin * 2));
        popup.style.left = `${Math.max(margin, Math.min(bounds.left, innerWidth - width - margin))}px`;
        popup.style.width = `${width}px`;
        popup.style.maxHeight = `${Math.max(above, below)}px`;
        if (below >= above) {
            popup.style.top = `${bounds.bottom}px`;
        } else {
            popup.style.bottom = `${innerHeight - bounds.top}px`;
        }
        popup.showPopover();
        list.focus({ preventScroll: true });
        highlight(selectedIndex);
        let search = "";
        let searchedAt = 0;
        list.addEventListener("keydown", event => {
            if (event.key === "Escape") {
                event.preventDefault();
                event.stopPropagation();
                close(true);
            } else if (event.key === "Enter" || event.key === " " || (event.altKey && event.key === "ArrowUp")) {
                event.preventDefault();
                event.stopPropagation();
                commit();
            } else if (event.key === "Tab") {
                event.preventDefault();
                event.stopPropagation();
                const controls = [...dialog.querySelectorAll("button, select, input")]
                    .filter(element => !element.disabled && element.getClientRects().length && !popup.contains(element));
                const index = controls.indexOf(source);
                const next = (index + (event.shiftKey ? -1 : 1) + controls.length) % controls.length;
                commit();
                controls[next].focus();
            } else if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
                event.preventDefault();
                const last = list.children.length - 1;
                highlight(event.key === "Home" ? 0 : event.key === "End" ? last :
                    Math.max(0, Math.min(last, selectedIndex + (event.key === "ArrowDown" ? 1 : -1))));
            } else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
                event.preventDefault();
                const now = Date.now();
                search = (now - searchedAt > 500 ? "" : search) + event.key.toLocaleLowerCase();
                searchedAt = now;
                const index = [...select.options].findIndex(option =>
                    option.textContent.trim().toLocaleLowerCase().startsWith(search));
                if (index >= 0) highlight(index);
            }
        });
    };

    const pointerdown = event => {
        const select = event.target.closest(".tw-settings select");
        if (select && !select.disabled && dialog.contains(select)) {
            event.preventDefault();
            open(select);
        } else if (popup && !popup.contains(event.target)) {
            close();
        }
    };
    const click = event => {
        const select = event.target.closest("select");
        if (select && !select.disabled && dialog.contains(select)) {
            event.preventDefault();
            if (source !== select) open(select);
        }
    };
    const keydown = event => {
        if (event.target.tagName !== "SELECT") return;
        if (event.key === " " || event.key === "Enter" || (event.altKey && event.key === "ArrowDown")) {
            event.preventDefault();
            open(event.target);
        }
    };
    const focusout = event => {
        if (popup && event.relatedTarget && !popup.contains(event.relatedTarget)) close();
    };
    const resized = () => close(true);
    const scrolled = event => {
        if (popup && event.target === dialog && Math.abs(source.getBoundingClientRect().top - anchorTop) > 1) close();
    };
    document.addEventListener("pointerdown", pointerdown);
    dialog.addEventListener("click", click);
    dialog.addEventListener("keydown", keydown);
    dialog.addEventListener("focusout", focusout);
    dialog.addEventListener("close", resized);
    dialog.addEventListener("scroll", scrolled);
    window.addEventListener("resize", resized);
    return () => {
        close();
        document.removeEventListener("pointerdown", pointerdown);
        dialog.removeEventListener("click", click);
        dialog.removeEventListener("keydown", keydown);
        dialog.removeEventListener("focusout", focusout);
        dialog.removeEventListener("close", resized);
        dialog.removeEventListener("scroll", scrolled);
        window.removeEventListener("resize", resized);
    };
}
