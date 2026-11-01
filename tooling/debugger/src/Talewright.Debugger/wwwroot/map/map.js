// Debugger Map tab. ELK lays out each area in a Web Worker, d3-zoom pans and zooms, and the map is painted on a
// canvas. The canvas is hidden from assistive technology: a roving group of scene buttons and the details panel carry
// the same scenes, diverts and hub offers, and stay synchronized with what the canvas highlights.
window.talewrightMap = (() => {
  const NODE_WIDTH = 200;
  const LINE_HEIGHT = 15;
  const AREA_GAP = 160;
  const AREA_PADDING = 40;
  const DETAIL_ZOOM = 0.35;
  const SANS = `system-ui, "DejaVu Sans", sans-serif`;
  const kindColors = { Scene: "#57606a", Choice: "#0969da", Success: "#1a7f37", Failure: "#cf222e", HubOffer: "#8250df" };

  let elk = null;
  const layoutEngine = () => elk || (elk = new ELK({ workerUrl: "map/vendor/elk-worker.min.js" }));

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function nodeSize(node) {
    if (node.end) return { width: 90, height: 34 };
    if (node.stub) return { width: Math.min(NODE_WIDTH, 24 + node.text[0].length * 7), height: 28 };
    return { width: NODE_WIDTH, height: 14 + node.text.length * LINE_HEIGHT };
  }

  // Children keep story order, with each tag group placed at its first member, so depth-first cycle breaking starts
  // from the start scene and hubs sit above the scenes that return to them.
  function elkGraph(view, area) {
    const children = [];
    const groups = new Map();
    const groupLabels = new Map(view.groups.map(group => [group.id, group.label]));
    const nodes = view.nodes.filter(node => node.area === area).sort((a, b) => Number(b.start) - Number(a.start));
    for (const node of nodes) {
      const child = { id: node.id, ...nodeSize(node) };
      if (!node.group) {
        children.push(child);
        continue;
      }
      if (!groups.has(node.group)) {
        const label = groupLabels.get(node.group);
        const group = {
          id: node.group, children: [],
          labels: [{ text: label, width: label.length * 7 + 10, height: 16 }],
          layoutOptions: { "elk.padding": "[top=30,left=12,bottom=12,right=12]", "elk.nodeLabels.placement": "INSIDE V_TOP H_LEFT" },
        };
        groups.set(node.group, group);
        children.push(group);
      }
      groups.get(node.group).children.push(child);
    }
    const lines = view.lines.filter(line => line.area === area);
    return {
      id: area,
      layoutOptions: {
        "elk.algorithm": "layered",
        "elk.direction": "DOWN",
        "elk.hierarchyHandling": "INCLUDE_CHILDREN",
        "elk.edgeRouting": "ORTHOGONAL",
        "elk.layered.spacing.nodeNodeBetweenLayers": "36",
        "elk.spacing.nodeNode": "18",
        "elk.layered.spacing.edgeNodeBetweenLayers": "12",
        "elk.layered.spacing.edgeEdgeBetweenLayers": "6",
        "elk.spacing.edgeEdge": "6",
        "elk.layered.nodePlacement.strategy": "LINEAR_SEGMENTS",
        "elk.layered.compaction.postCompaction.strategy": "EDGE_LENGTH",
        "elk.spacing.edgeLabel": "4",
        "elk.layered.considerModelOrder.strategy": "NODES_AND_EDGES",
        "elk.layered.cycleBreaking.strategy": "DEPTH_FIRST",
        "elk.json.edgeCoords": "ROOT",
        "elk.json.shapeCoords": "ROOT",
      },
      children,
      edges: lines.map(line => ({
        id: line.id, sources: [line.from], targets: [line.to],
        labels: line.label ? [{ text: line.label, width: Math.min(220, 8 + line.label.length * 6.2), height: 14 }] : [],
      })),
    };
  }

  // Lays out the main and unreachable areas separately and places the unreachable area to the right.
  async function layout(view) {
    const areas = ["main", "unreachable"].filter(area => view.nodes.some(node => node.area === area));
    const results = await Promise.all(areas.map(area => layoutEngine().layout(elkGraph(view, area))));
    const boxes = new Map();
    const lines = new Map();
    let unreachable = null;
    let width = 0, height = 0;
    results.forEach((result, index) => {
      const dx = index === 0 ? 0 : width + AREA_GAP + AREA_PADDING;
      const dy = index === 0 ? 0 : AREA_PADDING + 30;
      const walk = parent => {
        for (const child of parent.children || []) {
          boxes.set(child.id, { x: child.x + dx, y: child.y + dy, width: child.width, height: child.height });
          walk(child);
        }
      };
      walk(result);
      for (const edge of result.edges || []) {
        const move = point => ({ x: point.x + dx, y: point.y + dy });
        const sections = (edge.sections || []).map(s => [s.startPoint, ...(s.bendPoints || []), s.endPoint].map(move));
        const label = (edge.labels || [])[0];
        lines.set(edge.id, { sections, label: label && { ...move(label), width: label.width, height: label.height } });
      }
      if (areas[index] === "unreachable") {
        unreachable = { x: dx - AREA_PADDING, y: 0, width: result.width + 2 * AREA_PADDING, height: result.height + dy + AREA_PADDING };
        width = unreachable.x + unreachable.width;
        height = Math.max(height, unreachable.height);
      } else {
        width = result.width;
        height = Math.max(height, result.height);
      }
    });
    for (const [id, line] of lines) {
      const points = line.sections.flat();
      if (!points.length) throw new Error(`ELK returned no route for ${id}`);
      if (line.label) points.push(line.label, { x: line.label.x + line.label.width, y: line.label.y + line.label.height });
      const xs = points.map(p => p.x), ys = points.map(p => p.y);
      line.bounds = { x: Math.min(...xs) - 12, y: Math.min(...ys) - 12, x2: Math.max(...xs) + 12, y2: Math.max(...ys) + 12 };
    }
    return { boxes, lines, unreachable, width, height };
  }

  function palette(host) {
    const style = getComputedStyle(host);
    const value = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
    return {
      background: value("--tw-bg", "#faf8f3"), text: value("--tw-fg", "#222"), muted: value("--tw-muted", "#6b6558"),
      panel: value("--tw-panel", "#f0ebe0"), border: value("--tw-border", "#d8cfbd"), accent: value("--tw-accent", "#7a4b1f"),
      error: value("--tw-error", "#a12020"), warning: value("--tw-warning", "#8a6200"),
    };
  }

  const lineColor = line => kindColors[line.kind === "HubOffer" ? "HubOffer" : line.outcome || (line.kind === "Scene" || line.kind === "EntryCheck" ? "Scene" : "Choice")];
  const lineDash = line => line.kind === "HubOffer" ? [6, 4] : line.kind === "EntryCheck" ? [5, 3] : [];

  function create(host) {
    host.replaceChildren();
    const toolbar = element("div", "map-toolbar");
    const fitButton = element("button", null, "Fit map");
    fitButton.type = "button";
    const status = element("p", "map-status", "Laying out the map…");
    status.setAttribute("role", "status");
    toolbar.append(fitButton, status);

    const surface = element("div", "map-surface");
    const canvas = element("canvas");
    canvas.setAttribute("aria-hidden", "true");
    const tooltip = element("div", "map-tooltip");
    tooltip.setAttribute("aria-hidden", "true");
    tooltip.hidden = true;
    surface.append(canvas, tooltip);

    const scenes = element("div", "visually-hidden");
    scenes.setAttribute("role", "group");
    scenes.setAttribute("aria-label", "Scenes on the map. Use the arrow keys to move between scenes, and Home or End for the first or last.");

    const details = element("section", "map-details");
    details.setAttribute("aria-label", "Scene details");
    details.append(element("p", null, "Select a scene to list its diverts and hub offers."));

    host.append(toolbar, surface, scenes, details);

    const state = {
      host, canvas, context: canvas.getContext("2d"), surface, tooltip, status, scenes, details,
      view: null, laid: null, colors: palette(host), transform: d3.zoomIdentity, fitted: false, queued: false,
      selected: null, focused: null, highlighted: null, buttons: new Map(), generation: 0,
    };

    state.zoom = d3.zoom().scaleExtent([0.02, 4]).on("zoom", event => {
      state.transform = event.transform;
      redraw(state);
    });
    state.selection = d3.select(canvas).call(state.zoom).on("dblclick.zoom", null);

    new ResizeObserver(() => resize(state)).observe(canvas);
    fitButton.addEventListener("click", () => fit(state));
    canvas.addEventListener("click", event => {
      const node = nodeAt(state, event);
      if (node) select(state, node.stub ? node.id.slice(5) : node.id, false);
    });
    canvas.addEventListener("mousemove", event => hover(state, event));
    canvas.addEventListener("mouseleave", () => { state.tooltip.hidden = true; });
    return state;
  }

  function resize(state) {
    const ratio = window.devicePixelRatio || 1;
    state.canvas.width = Math.round(state.canvas.clientWidth * ratio);
    state.canvas.height = Math.round(state.canvas.clientHeight * ratio);
    if (state.laid && !state.fitted && state.canvas.clientWidth > 0) fit(state);
    redraw(state);
  }

  function redraw(state) {
    if (state.queued) return;
    state.queued = true;
    requestAnimationFrame(() => paint(state));
  }

  // One paint per animation frame, scaled for the display, skipping what is off screen.
  function paint(state) {
    state.queued = false;
    const { context, canvas, transform, colors } = state;
    const ratio = window.devicePixelRatio || 1;
    context.setTransform(1, 0, 0, 1, 0, 0);
    context.globalAlpha = 1;
    context.setLineDash([]);
    context.fillStyle = colors.background;
    context.fillRect(0, 0, canvas.width, canvas.height);
    if (!state.laid) return;
    context.setTransform(ratio * transform.k, 0, 0, ratio * transform.k, ratio * transform.x, ratio * transform.y);
    const [x, y] = transform.invert([0, 0]);
    const [x2, y2] = transform.invert([canvas.clientWidth, canvas.clientHeight]);
    draw(state, { k: transform.k, x, y, x2, y2 });
  }

  function draw(state, box) {
    const { context: ctx, view, laid, colors } = state;
    const visible = (b, w = b.width, h = b.height) => b.x < box.x2 && b.x + w > box.x && b.y < box.y2 && b.y + h > box.y;
    const detail = box.k >= DETAIL_ZOOM;
    const width = value => Math.max(value, 0.45 / box.k);
    const text = (value, x, y, font, fill, align = "left") => {
      ctx.font = font;
      ctx.fillStyle = fill;
      ctx.textAlign = align;
      ctx.fillText(value, x, y);
    };
    const rect = (b, radius, fill, stroke, lineWidth, dash = []) => {
      ctx.beginPath();
      ctx.roundRect(b.x, b.y, b.width, b.height, radius);
      if (fill) { ctx.fillStyle = fill; ctx.fill(); }
      if (stroke) {
        ctx.setLineDash(dash);
        ctx.lineWidth = width(lineWidth);
        ctx.strokeStyle = stroke;
        ctx.stroke();
        ctx.setLineDash([]);
      }
    };

    if (laid.unreachable && visible(laid.unreachable)) {
      const area = laid.unreachable;
      rect(area, 12, null, colors.error, 1.5, [8, 5]);
      if (detail) {
        text(`Unreachable from the start: ${view.unreachableScenes}`, area.x + 16, area.y + 24, `bold 14px ${SANS}`, colors.error);
        text("Reachability follows diverts and hub offers; it doesn't check whether conditions can be met.",
          area.x + 16, area.y + 44, `11px ${SANS}`, colors.muted);
      }
    }

    for (const group of view.groups) {
      const b = laid.boxes.get(group.id);
      if (!visible(b)) continue;
      ctx.globalAlpha = 0.12;
      rect(b, 8, kindColors.HubOffer);
      ctx.globalAlpha = 1;
      rect(b, 8, null, kindColors.HubOffer, 1.2);
      if (detail) text(`${group.label}  ·  ${state.groupSizes.get(group.id)} scenes`, b.x + 10, b.y + 18, `bold 12px ${SANS}`, kindColors.HubOffer);
    }

    const labels = [];
    const drawLine = (line, highlighted) => {
      const { sections, label, bounds } = laid.lines.get(line.id);
      if (!visible(bounds, bounds.x2 - bounds.x, bounds.y2 - bounds.y)) return;
      const color = highlighted ? colors.accent : lineColor(line);
      ctx.strokeStyle = color;
      ctx.lineWidth = width(highlighted ? 4 : line.kind === "HubOffer" ? 1.4 : 1.2);
      ctx.setLineDash(highlighted ? [] : lineDash(line));
      for (const section of sections) {
        ctx.beginPath();
        section.forEach((p, i) => i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y));
        ctx.stroke();
      }
      ctx.setLineDash([]);
      const last = sections.at(-1), tip = last.at(-1), from = last.at(-2);
      const length = Math.hypot(tip.x - from.x, tip.y - from.y) || 1;
      const ux = (tip.x - from.x) / length, uy = (tip.y - from.y) / length, size = highlighted ? 11 : 8;
      ctx.beginPath();
      ctx.moveTo(tip.x, tip.y);
      ctx.lineTo(tip.x - ux * size - uy * size / 2, tip.y - uy * size + ux * size / 2);
      ctx.lineTo(tip.x - ux * size + uy * size / 2, tip.y - uy * size - ux * size / 2);
      ctx.closePath();
      ctx.fillStyle = color;
      ctx.fill();
      if (detail && line.label && label) labels.push({ line, label, highlighted });
    };
    for (const line of view.lines) if (line.id !== state.highlighted) drawLine(line, false);
    if (state.highlighted) drawLine(state.lines.get(state.highlighted), true);
    for (const { line, label, highlighted } of labels) {
      ctx.globalAlpha = 0.9;
      rect({ x: label.x, y: label.y, width: label.width, height: 14 }, 3, colors.background);
      ctx.globalAlpha = 1;
      text(line.label, label.x + 4, label.y + 10.5, `${line.kind === "HubOffer" || highlighted ? "bold " : ""}10px monospace`,
        highlighted ? colors.accent : line.kind === "HubOffer" ? kindColors.HubOffer : colors.muted);
    }

    for (const node of view.nodes) {
      const b = laid.boxes.get(node.id);
      if (!visible(b)) continue;
      let fill = colors.background, stroke = colors.muted, lineWidth = 1.2, dash = [];
      if (node.hub) { fill = colors.panel; stroke = colors.warning; lineWidth = 1.6; }
      if (node.start) { stroke = kindColors.Choice; lineWidth = 2.5; }
      if (node.end) fill = stroke = colors.text;
      if (node.stub) { fill = colors.panel; stroke = colors.border; dash = [4, 3]; }
      else if (!node.reachable) { stroke = colors.error; lineWidth = 2; dash = [4, 3]; }
      if (node.id === state.selected) { stroke = colors.accent; lineWidth = 4; dash = []; }
      rect(b, node.end ? 17 : 5, fill, stroke, lineWidth, dash);
      if (node.id === state.focused) {
        rect({ x: b.x - 5, y: b.y - 5, width: b.width + 10, height: b.height + 10 }, 8, null, colors.accent, 2, [3, 2]);
      }
      if (!detail) continue;
      if (node.end) {
        text("END", b.x + b.width / 2, b.y + 21, `bold 11px ${SANS}`, colors.background, "center");
        continue;
      }
      if (node.stub) {
        text(node.text[0], b.x + 8, b.y + 18, "11px monospace", colors.muted);
        continue;
      }
      node.text.forEach((line, i) => text(line, b.x + 8, b.y + 18 + i * LINE_HEIGHT,
        i === 0 ? "10.5px monospace" : i === 2 ? "10px monospace" : `11px ${SANS}`,
        i === 0 ? colors.muted : i === 2 ? kindColors.HubOffer : colors.text));
      const badges = [node.start && "START", node.hub && "HUB", node.once && "ONCE", !node.reachable && "UNREACHABLE"].filter(Boolean);
      if (badges.length) text(badges.join(" · "), b.x + b.width - 6, b.y + 12, `bold 9px ${SANS}`, colors.warning, "right");
    }
  }

  function nodeAt(state, event) {
    if (!state.laid) return null;
    const [x, y] = state.transform.invert([event.offsetX, event.offsetY]);
    return state.view.nodes.find(node => {
      const b = state.laid.boxes.get(node.id);
      return x >= b.x && x <= b.x + b.width && y >= b.y && y <= b.y + b.height;
    }) || null;
  }

  function lineAt(state, event) {
    if (!state.laid) return null;
    const [x, y] = state.transform.invert([event.offsetX, event.offsetY]);
    const tolerance = 6 / state.transform.k;
    let nearest = null, best = tolerance * tolerance;
    for (const line of state.view.lines) {
      const { sections, label, bounds } = state.laid.lines.get(line.id);
      if (x + tolerance < bounds.x || x - tolerance > bounds.x2 || y + tolerance < bounds.y || y - tolerance > bounds.y2) continue;
      if (state.transform.k >= DETAIL_ZOOM && label && x >= label.x && x <= label.x + label.width && y >= label.y && y <= label.y + label.height) return line;
      for (const section of sections) {
        for (let i = 1; i < section.length; i++) {
          const a = section[i - 1], b = section[i], dx = b.x - a.x, dy = b.y - a.y;
          const length = dx * dx + dy * dy;
          const t = length ? Math.max(0, Math.min(1, ((x - a.x) * dx + (y - a.y) * dy) / length)) : 0;
          const distance = (x - a.x - t * dx) ** 2 + (y - a.y - t * dy) ** 2;
          if (distance <= best) { best = distance; nearest = line; }
        }
      }
    }
    return nearest;
  }

  // Hover shows the same descriptions the accessible controls announce, including full conditions.
  function hover(state, event) {
    const { canvas, tooltip } = state;
    if (event.buttons) { canvas.style.cursor = "grabbing"; tooltip.hidden = true; return; }
    const node = nodeAt(state, event);
    const line = node ? null : lineAt(state, event);
    canvas.style.cursor = node ? "pointer" : line ? "help" : "grab";
    const lines = node ? [node.name] : line ? line.items : [];
    if (!lines.length) { tooltip.hidden = true; return; }
    tooltip.replaceChildren(...lines.map(value => element("div", null, value)));
    tooltip.hidden = false;
    const left = Math.min(event.offsetX + 14, canvas.clientWidth - tooltip.offsetWidth - 4);
    const top = event.offsetY + 18 + tooltip.offsetHeight > canvas.clientHeight ? event.offsetY - tooltip.offsetHeight - 8 : event.offsetY + 18;
    tooltip.style.left = `${Math.max(4, left)}px`;
    tooltip.style.top = `${Math.max(4, top)}px`;
  }

  function fit(state) {
    const { canvas, laid } = state;
    if (!laid || !canvas.clientWidth) return;
    state.fitted = true;
    const scale = Math.min(canvas.clientWidth / (laid.width + 40), canvas.clientHeight / (laid.height + 40), 1.5);
    state.selection.call(state.zoom.transform, d3.zoomIdentity
      .translate((canvas.clientWidth - laid.width * scale) / 2, (canvas.clientHeight - laid.height * scale) / 2).scale(scale));
  }

  function center(state, id, scale) {
    const b = state.laid && state.laid.boxes.get(id);
    if (!b || !state.canvas.clientWidth) return;
    state.selection.call(state.zoom.transform, d3.zoomIdentity
      .translate(state.canvas.clientWidth / 2 - (b.x + b.width / 2) * scale, state.canvas.clientHeight / 2 - (b.y + b.height / 2) * scale)
      .scale(scale));
  }

  function select(state, id, moveFocus) {
    const button = state.buttons.get(id);
    if (!button) return;
    for (const other of state.buttons.values()) other.tabIndex = other === button ? 0 : -1;
    if (moveFocus) {
      button.focus();
      return;
    }
    state.selected = id;
    state.highlighted = null;
    showDetails(state, state.nodes.get(id));
    redraw(state);
  }

  function itemButton(state, item) {
    const button = element("button", "map-item", item.name);
    button.type = "button";
    button.dataset.item = item.id;
    button.addEventListener("focus", () => { state.highlighted = item.line; redraw(state); });
    button.addEventListener("mouseenter", () => { state.highlighted = item.line; redraw(state); });
    const clear = () => {
      if (state.highlighted === item.line && document.activeElement !== button) { state.highlighted = null; redraw(state); }
    };
    button.addEventListener("mouseleave", clear);
    button.addEventListener("blur", () => { state.highlighted = null; redraw(state); });
    button.addEventListener("click", () => select(state, item.target, true));
    const entry = element("li");
    entry.append(button);
    return entry;
  }

  function showDetails(state, node) {
    const { details } = state;
    const heading = element("h3", null, node.name);
    const diverts = node.items.filter(item => !item.hubOffer);
    const offers = node.items.filter(item => item.hubOffer);
    const list = (title, items) => {
      const section = [element("h4", null, `${title}: ${items.length}`)];
      if (items.length) {
        const ul = element("ul");
        ul.setAttribute("aria-label", title);
        ul.append(...items.map(item => itemButton(state, item)));
        section.push(ul);
      }
      return section;
    };
    details.replaceChildren(heading, ...list("Diverts", diverts), ...(node.hub || offers.length ? list("Hub offers", offers) : []));
  }

  function buildSceneButtons(state) {
    const focusedId = state.scenes.contains(document.activeElement) ? document.activeElement.dataset.id : null;
    const nodes = state.view.nodes.filter(node => !node.stub);
    const buttons = nodes.map((node, index) => {
      const button = element("button", null, node.name);
      button.type = "button";
      button.dataset.id = node.id;
      button.tabIndex = -1;
      button.addEventListener("focus", () => {
        state.focused = node.id;
        select(state, node.id, false);
        center(state, node.id, Math.max(state.transform.k, 0.8));
      });
      button.addEventListener("blur", () => { state.focused = null; redraw(state); });
      button.addEventListener("keydown", event => {
        let next;
        if (event.key === "ArrowRight" || event.key === "ArrowDown") next = Math.min(index + 1, buttons.length - 1);
        else if (event.key === "ArrowLeft" || event.key === "ArrowUp") next = Math.max(index - 1, 0);
        else if (event.key === "Home") next = 0;
        else if (event.key === "End") next = buttons.length - 1;
        else return;
        event.preventDefault();
        buttons[next].focus();
      });
      return button;
    });
    state.scenes.replaceChildren(...buttons);
    state.buttons = new Map(buttons.map(button => [button.dataset.id, button]));
    const current = state.buttons.get(state.selected) || buttons[0];
    if (current) current.tabIndex = 0;
    if (focusedId && state.buttons.has(focusedId)) state.buttons.get(focusedId).focus();
  }

  // Renders a MapView from MapTab.razor. Called again with the new view whenever the debugger reloads the story.
  async function render(host, view) {
    const state = host.talewrightMap || (host.talewrightMap = create(host));
    const generation = ++state.generation;
    state.status.textContent = "Laying out the map…";
    let laid;
    try {
      laid = await layout(view);
    } catch (error) {
      if (generation === state.generation) state.status.textContent = `The map could not be laid out: ${error.message || error}`;
      throw error;
    }
    if (generation !== state.generation) return;
    state.view = view;
    state.laid = laid;
    state.nodes = new Map(view.nodes.map(node => [node.id, node]));
    state.lines = new Map(view.lines.map(line => [line.id, line]));
    state.groupSizes = new Map(view.groups.map(group => [group.id, view.nodes.filter(node => node.group === group.id).length]));
    state.highlighted = null;
    if (!state.nodes.has(state.selected)) state.selected = null;
    buildSceneButtons(state);
    if (state.selected) showDetails(state, state.nodes.get(state.selected));
    else state.details.replaceChildren(element("p", null, "Select a scene to list its diverts and hub offers."));
    const scenes = view.nodes.filter(node => !node.end && !node.stub).length;
    const diverts = view.nodes.reduce((sum, node) => sum + node.items.filter(item => !item.hubOffer).length, 0);
    const offers = view.nodes.reduce((sum, node) => sum + node.items.length, 0) - diverts;
    const count = (n, one, many) => `${n} ${n === 1 ? one : many}`;
    state.status.textContent = `${count(scenes, "scene", "scenes")}, ${count(diverts, "divert", "diverts")}, ` +
      `${count(offers, "hub offer", "hub offers")}, ${view.unreachableScenes} unreachable.`;
    if (!state.fitted) fit(state);
    redraw(state);
  }

  return { render };
})();
