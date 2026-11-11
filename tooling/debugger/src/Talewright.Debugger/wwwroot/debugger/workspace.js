const layoutStorageKey = "talewright.debugger.layout";
const splitters = new Map();
const inspectors = new Map();
let nextId = 0;
let inspectEnabled = false;

export function loadLayout() {
  const raw = localStorage.getItem(layoutStorageKey);
  if (raw === null) {
    return {
      viewerWidth: 70,
      mainHeight: 68,
      inspectorCollapsed: false,
      toolsCollapsed: false,
      connectionsExpanded: true,
      windowWidth: window.innerWidth,
      hasStoredLayout: false,
    };
  }

  const layout = JSON.parse(raw);
  if (!Number.isFinite(layout.viewerWidth) || layout.viewerWidth < 20 || layout.viewerWidth > 80 ||
      !Number.isFinite(layout.mainHeight) || layout.mainHeight < 20 || layout.mainHeight > 80 ||
      typeof layout.inspectorCollapsed !== "boolean" || typeof layout.toolsCollapsed !== "boolean" ||
      (layout.connectionsExpanded !== undefined && typeof layout.connectionsExpanded !== "boolean")) {
    throw new Error("Stored debugger layout is invalid.");
  }
  return {
    ...layout,
    connectionsExpanded: layout.connectionsExpanded ?? true,
    windowWidth: window.innerWidth,
    hasStoredLayout: true,
  };
}

export function saveLayout(layout) {
  localStorage.setItem(layoutStorageKey, JSON.stringify({
    viewerWidth: layout.viewerWidth,
    mainHeight: layout.mainHeight,
    inspectorCollapsed: layout.inspectorCollapsed,
    toolsCollapsed: layout.toolsCollapsed,
    connectionsExpanded: layout.connectionsExpanded,
  }));
}

export function attachSplitter(container, separator, dotnet, vertical) {
  const id = ++nextId;
  let activePointer = null;
  let frame = 0;
  let nextSize = 0;
  const resize = event => {
    if (activePointer !== event.pointerId)
      return;
    const bounds = container.getBoundingClientRect();
    const length = vertical ? bounds.height : bounds.width;
    const position = vertical ? event.clientY - bounds.top : event.clientX - bounds.left;
    nextSize = Math.max(20, Math.min(80, position / length * 100));
    if (!frame) {
      frame = requestAnimationFrame(() => {
        frame = 0;
        dotnet.invokeMethodAsync("SetSize", nextSize);
      });
    }
  };
  const start = event => {
    if (event.button !== 0)
      return;
    activePointer = event.pointerId;
    separator.setPointerCapture(event.pointerId);
    event.preventDefault();
  };
  const stop = event => {
    if (activePointer !== event.pointerId)
      return;
    activePointer = null;
    if (separator.hasPointerCapture(event.pointerId))
      separator.releasePointerCapture(event.pointerId);
  };

  separator.addEventListener("pointerdown", start);
  separator.addEventListener("pointermove", resize);
  separator.addEventListener("pointerup", stop);
  separator.addEventListener("pointercancel", stop);
  splitters.set(id, () => {
    separator.removeEventListener("pointerdown", start);
    separator.removeEventListener("pointermove", resize);
    separator.removeEventListener("pointerup", stop);
    separator.removeEventListener("pointercancel", stop);
    if (frame)
      cancelAnimationFrame(frame);
  });
  return id;
}

export function detachSplitter(id) {
  splitters.get(id)?.();
  splitters.delete(id);
}

export function attachInspector(container, dotnet) {
  const id = ++nextId;
  const overlay = container.querySelector(".inspect-overlay");
  const state = { container, dotnet, overlay, enabled: inspectEnabled };
  const targetFor = event => {
    const target = event.target instanceof Element
      ? event.target.closest("[data-tw-choice], [data-tw-scene]")
      : null;
    return target && container.contains(target) ? target : null;
  };
  const showOutline = event => {
    if (!state.enabled)
      return;
    const target = targetFor(event);
    if (!target) {
      overlay.hidden = true;
      return;
    }
    const bounds = target.getBoundingClientRect();
    const parent = container.getBoundingClientRect();
    overlay.style.left = `${bounds.left - parent.left + container.scrollLeft}px`;
    overlay.style.top = `${bounds.top - parent.top + container.scrollTop}px`;
    overlay.style.width = `${bounds.width}px`;
    overlay.style.height = `${bounds.height}px`;
    overlay.hidden = false;
  };
  const hideOutline = event => {
    if (!event.relatedTarget || !container.contains(event.relatedTarget))
      overlay.hidden = true;
  };
  const select = event => {
    if (!state.enabled)
      return false;
    const target = targetFor(event);
    if (!target)
      return false;
    const sceneElement = target.matches("[data-tw-scene]")
      ? target
      : target.closest("[data-tw-scene]");
    const scene = sceneElement?.getAttribute("data-tw-scene");
    if (!scene)
      return false;
    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation();
    const choice = target.getAttribute("data-tw-choice");
    state.enabled = false;
    overlay.hidden = true;
    inspectEnabled = false;
    state.dotnet.invokeMethodAsync("Select", scene, choice);
    state.dotnet.invokeMethodAsync("InspectModeChanged", false);
    return true;
  };
  const click = event => { select(event); };
  const keydown = event => {
    if ((event.key === "Enter" || event.key === " ") &&
        event.target instanceof Element && event.target.closest("[data-tw-choice]")) {
      select(event);
    }
  };
  const escape = event => {
    if (event.key === "Escape" && state.enabled) {
      event.preventDefault();
      state.enabled = false;
      overlay.hidden = true;
      inspectEnabled = false;
      state.dotnet.invokeMethodAsync("InspectModeChanged", false);
    }
  };
  const shortcut = event => {
    if (event.ctrlKey && event.shiftKey && event.key.toLowerCase() === "c" && !event.repeat) {
      event.preventDefault();
      inspectEnabled = !inspectEnabled;
      for (const item of inspectors.values()) {
        item.enabled = inspectEnabled;
        item.overlay.hidden = true;
        item.dotnet.invokeMethodAsync("InspectModeChanged", inspectEnabled);
      }
    }
  };
  container.addEventListener("pointerover", showOutline, true);
  container.addEventListener("pointermove", showOutline, true);
  container.addEventListener("pointerout", hideOutline, true);
  container.addEventListener("click", click, true);
  container.addEventListener("keydown", keydown, true);
  window.addEventListener("keydown", escape, true);
  window.addEventListener("keydown", shortcut, true);
  state.detach = () => {
    container.removeEventListener("pointerover", showOutline, true);
    container.removeEventListener("pointermove", showOutline, true);
    container.removeEventListener("pointerout", hideOutline, true);
    container.removeEventListener("click", click, true);
    container.removeEventListener("keydown", keydown, true);
    window.removeEventListener("keydown", escape, true);
    window.removeEventListener("keydown", shortcut, true);
  };
  inspectors.set(id, state);
  return id;
}

export function setInspectMode(enabled) {
  inspectEnabled = enabled;
  for (const state of inspectors.values()) {
    state.enabled = enabled;
    state.overlay.hidden = true;
  }
}

export function detachInspector(id) {
  inspectors.get(id)?.detach();
  inspectors.delete(id);
}
