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
  const SCENE_NAVIGATION_KEYS = new Set(["ArrowRight", "ArrowDown", "ArrowLeft", "ArrowUp", "Home", "End"]);
  const kindColors = { Scene: "#57606a", Choice: "#0969da", Success: "#1a7f37", Failure: "#cf222e", HubOffer: "#8250df" };

  let elk = null;
  const layoutEngine = () => elk || (elk = new ELK({ workerUrl: "map/vendor/elk-worker.min.js" }));

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

  function palette(probe) {
    // Custom properties retain light-dark() rather than a canvas-compatible computed color.
    const previousColor = probe.style.color;
    const value = (name, fallback) => {
      probe.style.color = `var(${name}, ${fallback})`;
      return getComputedStyle(probe).color;
    };
    try {
      return {
        background: value("--tw-bg", "#faf8f3"), text: value("--tw-fg", "#222"), muted: value("--tw-muted", "#6b6558"),
        panel: value("--tw-panel", "#f0ebe0"), border: value("--tw-border", "#d8cfbd"), accent: value("--tw-accent", "#7a4b1f"),
        error: value("--tw-error", "#a12020"), warning: value("--tw-warning", "#8a6200"),
      };
    } finally {
      probe.style.color = previousColor;
    }
  }

  const lineColor = line => kindColors[line.kind === "HubOffer" ? "HubOffer" : line.outcome || (line.kind === "Scene" || line.kind === "EntryCheck" ? "Scene" : "Choice")];
  const lineDash = line => line.kind === "HubOffer" ? [6, 4] : line.kind === "EntryCheck" ? [5, 3] : [];

  function create(host, canvas, tooltip) {
    const state = {
      host, canvas, context: canvas.getContext("2d"), tooltip,
      view: null, laid: null, colors: palette(tooltip), transform: d3.zoomIdentity, fitted: false, queued: false,
      selected: null, focused: null, highlighted: null, generation: 0,
      run: null, routeVisits: new Map(), routeLines: new Map(), dotnet: null, summary: "",
      active: false, followsCurrent: true,
    };

    state.zoom = d3.zoom().scaleExtent([0.02, 4]).on("zoom", event => {
      state.transform = event.transform;
      if (event.sourceEvent && state.followsCurrent) {
        state.followsCurrent = false;
        reportStatus(state, statusText(state, state.run))
          ?.catch(error => console.error("Could not update map status.", error));
      }
      redraw(state);
    });
    state.selection = d3.select(canvas).call(state.zoom).on("dblclick.zoom", null);

    new ResizeObserver(() => resize(state)).observe(canvas);
    canvas.addEventListener("click", event => {
      const node = nodeAt(state, event);
      if (node) select(state, node.stub ? node.id.slice(5) : node.id);
    });
    canvas.addEventListener("mousemove", event => hover(state, event));
    canvas.addEventListener("mouseleave", () => { tooltip.hidden = true; });
    // MapSceneNavigator moves focus in .NET, but only a synchronous listener can stop these keys scrolling the page.
    host.addEventListener("keydown", event => {
      if (SCENE_NAVIGATION_KEYS.has(event.key) && event.target.closest?.(".map-scenes"))
        event.preventDefault();
    });
    return state;
  }

  function reportStatus(state, text) {
    return state.dotnet?.invokeMethodAsync("SetMapStatus", text);
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

  function draw(state, box, context = state.context) {
    const { view, laid, colors } = state;
    const ctx = context;
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
      const taken = state.routeLines.has(line.id);
      const color = highlighted ? colors.accent : taken ? kindColors.Success : lineColor(line);
      ctx.strokeStyle = color;
      ctx.lineWidth = width(highlighted ? 4 : taken ? 3 : line.kind === "HubOffer" ? 1.4 : 1.2);
      ctx.setLineDash(highlighted || taken ? [] : lineDash(line));
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
    if (detail) {
      for (const [id, orders] of state.routeLines) {
        const line = state.lines.get(id);
        const geometry = laid.lines.get(id);
        if (!line || !geometry || !visible(geometry.bounds,
          geometry.bounds.x2 - geometry.bounds.x, geometry.bounds.y2 - geometry.bounds.y)) continue;
        const section = geometry.sections.reduce((best, candidate) =>
          candidate.length > best.length ? candidate : best);
        const point = section[Math.floor(section.length / 2)];
        const label = `${orders[0]}${orders.length > 1 ? ` ×${orders.length}` : ""}`;
        ctx.beginPath();
        ctx.arc(point.x, point.y, 8 / box.k, 0, Math.PI * 2);
        ctx.fillStyle = kindColors.Success;
        ctx.fill();
        ctx.lineWidth = width(1);
        ctx.strokeStyle = colors.background;
        ctx.stroke();
        text(label, point.x, point.y + 3, `bold 9px ${SANS}`, colors.background, "center");
      }
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
      const visits = state.routeVisits.get(node.id);
      if (visits) { stroke = kindColors.Success; lineWidth = 2.8; dash = []; }
      if (state.run?.currentScene === node.id) { stroke = colors.accent; lineWidth = 4; dash = []; }
      if (state.run?.startedMidStory && state.run.startScene === node.id) { stroke = kindColors.Choice; lineWidth = 3; dash = []; }
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
      if (detail && visits) {
        const label = `${visits.first}${visits.count > 1 ? ` ×${visits.count}` : ""}`;
        text(label, b.x + b.width - 6, b.y + b.height - 4, `bold 9px ${SANS}`, kindColors.Success, "right");
      }
      const badges = [
        node.start && "START",
        state.run?.startedMidStory && state.run.startScene === node.id && "RUN START",
        state.run?.currentScene === node.id && "CURRENT",
        node.id === state.selected && state.run?.currentScene !== node.id && "SELECTED",
        node.hub && "HUB",
        node.once && "ONCE",
        !node.reachable && "UNREACHABLE",
      ].filter(Boolean);
      if (badges.length) text(badges.join(" · "), b.x + b.width - 6, b.y + 12, `bold 9px ${SANS}`, colors.warning, "right");
    }
  }

  function createSvgContext() {
    const output = [];
    let path = [];
    const context = {
      globalAlpha: 1,
      fillStyle: "#000",
      strokeStyle: "#000",
      lineWidth: 1,
      lineDash: [],
      font: "10px sans-serif",
      textAlign: "left",
      setLineDash(value) { this.lineDash = value; },
      beginPath() { path = []; },
      moveTo(x, y) { path.push(`M${x} ${y}`); },
      lineTo(x, y) { path.push(`L${x} ${y}`); },
      closePath() { path.push("Z"); },
      roundRect(x, y, width, height, radius) {
        const r = Math.min(radius, width / 2, height / 2);
        path.push(`M${x + r} ${y}H${x + width - r}A${r} ${r} 0 0 1 ${x + width} ${y + r}` +
          `V${y + height - r}A${r} ${r} 0 0 1 ${x + width - r} ${y + height}` +
          `H${x + r}A${r} ${r} 0 0 1 ${x} ${y + height - r}V${y + r}A${r} ${r} 0 0 1 ${x + r} ${y}Z`);
      },
      arc(x, y, radius, start, end) {
        if (Math.abs(end - start) < Math.PI * 2 - 0.0001)
          throw new Error("SVG map export only supports full-circle arcs.");
        path.push(`M${x + radius} ${y}A${radius} ${radius} 0 1 1 ${x - radius} ${y}` +
          `A${radius} ${radius} 0 1 1 ${x + radius} ${y}`);
      },
      fillRect(x, y, width, height) {
        output.push(`<rect x="${x}" y="${y}" width="${width}" height="${height}" fill="${escapeXml(this.fillStyle)}" opacity="${this.globalAlpha}"/>`);
      },
      fill() { this.emitPath("fill", this.fillStyle); },
      stroke() { this.emitPath("stroke", this.strokeStyle); },
      fillText(value, x, y) {
        const anchor = this.textAlign === "center" ? "middle" : this.textAlign === "right" ? "end" : "start";
        output.push(`<text x="${x}" y="${y}" text-anchor="${anchor}" style="font: ${escapeXml(this.font)}" ` +
          `fill="${escapeXml(this.fillStyle)}" opacity="${this.globalAlpha}">${escapeXml(value)}</text>`);
      },
      emitPath(kind, color) {
        const d = path.join("");
        const dash = this.lineDash.length ? ` stroke-dasharray="${this.lineDash.join(" ")}"` : "";
        const attributes = kind === "fill"
          ? `fill="${escapeXml(color)}" opacity="${this.globalAlpha}"`
          : `fill="none" stroke="${escapeXml(color)}" stroke-width="${this.lineWidth}"` +
            ` stroke-linecap="butt" stroke-linejoin="miter" opacity="${this.globalAlpha}"${dash}`;
        output.push(`<path d="${d}" ${attributes}/>`);
      },
      toString(width, height, background, padding) {
        return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" ` +
          `viewBox="0 0 ${width} ${height}" role="img" aria-label="Talewright story map">` +
          `<rect width="100%" height="100%" fill="${escapeXml(background)}"/>` +
          `<g transform="translate(${padding},${padding})">${output.join("")}</g></svg>`;
      },
    };
    return context;
  }

  function escapeXml(value) {
    return String(value).replace(/[&<>"']/g, character => ({
      "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&apos;",
    })[character]);
  }

  async function exportMap(host, format, dotnet, padding, maxPngDimension, maxPngPixels) {
    const state = host.talewrightMap;
    if (!state?.laid) throw new Error("The story map is not ready to export.");
    if (format !== "svg" && format !== "png") throw new Error(`Unsupported map export format: ${format}`);
    const width = Math.ceil(state.laid.width + padding * 2);
    const height = Math.ceil(state.laid.height + padding * 2);
    const box = { k: 1, x: -padding, y: -padding, x2: state.laid.width + padding, y2: state.laid.height + padding };
    let blob;

    if (format === "svg") {
      const context = createSvgContext();
      draw(state, box, context);
      blob = new Blob([context.toString(width, height, state.colors.background, padding)], { type: "image/svg+xml;charset=utf-8" });
    } else {
      if (!Number.isSafeInteger(width) || !Number.isSafeInteger(height)
        || width > maxPngDimension || height > maxPngDimension || width * height > maxPngPixels) {
        throw new Error(`PNG export is limited to ${maxPngDimension.toLocaleString()} pixels per side and ` +
          `${maxPngPixels.toLocaleString()} total pixels. Use SVG export for a full-resolution image of this map.`);
      }
      const size = await dotnet.invokeMethodAsync("CheckPngExportSize", width, height);
      if (!size.allowed) throw new Error(size.message);

      const canvas = document.createElement("canvas");
      canvas.width = size.width;
      canvas.height = size.height;
      try {
        const context = canvas.getContext("2d", { alpha: false });
        if (!context) throw new Error("The browser could not create a PNG canvas. Try SVG export instead.");
        context.fillStyle = state.colors.background;
        context.fillRect(0, 0, canvas.width, canvas.height);
        context.translate(padding, padding);
        draw(state, box, context);
        blob = await new Promise(resolve => canvas.toBlob(resolve, "image/png"));
      } finally {
        canvas.width = canvas.height = 0;
      }
      if (!blob) throw new Error("The browser could not encode the map as PNG. Try SVG export instead.");
    }

    const stream = DotNet.createJSStreamReference(blob);
    await dotnet.invokeMethodAsync("SaveMapExport", format, stream);
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
    tooltip.replaceChildren(...lines.map(value => {
      const description = document.createElement("div");
      description.textContent = value;
      return description;
    }));
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

  function select(state, id) {
    if (!state.nodes.has(id)) return;
    state.selected = id;
    state.highlighted = null;
    state.dotnet?.invokeMethodAsync("SelectScene", id).catch(error => {
      console.error("Could not update the selected scene preview.", error);
      reportStatus(state, "The selected scene preview could not be updated.")
        ?.catch(statusError => console.error("Could not update map status.", statusError));
    });
    redraw(state);
  }

  function statusText(state, run) {
    const start = run?.startedMidStory ? ` Started at mid-story scene ${run.startScene} from Scene setup.` : "";
    const current = run?.currentScene ? ` Current scene: ${run.currentScene}.` : "";
    const ended = run?.isEnded ? " The playtest has ended." : "";
    const paused = state.followsCurrent ? "" : " Following is paused; activate Focus current scene to resume.";
    return `${state.summary}${start}${current}${ended}${paused}`;
  }

  function followCurrent(state, scene) {
    if (!scene || !state.nodes?.has(scene)) return;
    center(state, scene, Math.max(state.transform.k, 0.8));
  }

  function focusCurrent(state, scene) {
    state.followsCurrent = true;
    followCurrent(state, scene || state.run?.currentScene);
    reportStatus(state, statusText(state, state.run))
      ?.catch(error => console.error("Could not update map status.", error));
  }

  async function updateRun(host, run) {
    const state = host.talewrightMap;
    if (!state) throw new Error("The story map has not been rendered.");
    const previousCurrent = state.run?.currentScene;
    state.run = run;
    state.routeVisits = new Map();
    state.routeLines = new Map();
    for (const step of run?.steps || []) {
      if (step.scene !== "END") {
        const visits = state.routeVisits.get(step.scene) || { first: step.order, count: 0 };
        visits.count++;
        state.routeVisits.set(step.scene, visits);
      }
      if (step.lineId) {
        const orders = state.routeLines.get(step.lineId) || [];
        orders.push(step.order);
        state.routeLines.set(step.lineId, orders);
      }
    }
    if (state.active && state.followsCurrent && run?.currentScene !== previousCurrent)
      followCurrent(state, run?.currentScene);
    await reportStatus(state, statusText(state, run));
    redraw(state);
  }

  // Renders map data from MapTab.razor; all accessible controls are owned by Razor.
  async function render(host, canvas, tooltip, view, run, selected, dotnet, active) {
    const state = host.talewrightMap || (host.talewrightMap = create(host, canvas, tooltip));
    const generation = ++state.generation;
    state.dotnet = dotnet;
    state.active = active;
    if (selected) state.selected = selected;
    await reportStatus(state, "Laying out the map…");
    let laid;
    try {
      laid = await layout(view);
    } catch (error) {
      if (generation === state.generation)
        await reportStatus(state, `The map could not be laid out: ${error.message || error}`);
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
    if (!state.nodes.has(state.focused)) state.focused = null;
    const scenes = view.nodes.filter(node => !node.end && !node.stub).length;
    const diverts = view.nodes.reduce((sum, node) => sum + node.items.filter(item => !item.hubOffer).length, 0);
    const offers = view.nodes.reduce((sum, node) => sum + node.items.length, 0) - diverts;
    const count = (n, one, many) => `${n} ${n === 1 ? one : many}`;
    state.summary = `${count(scenes, "scene", "scenes")}, ${count(diverts, "divert", "diverts")}, ` +
      `${count(offers, "hub offer", "hub offers")}, ${view.unreachableScenes} unreachable.`;
    if (!state.fitted) {
      const current = run?.currentScene || selected;
      if (active && state.followsCurrent && current && state.nodes.has(current)) {
        state.selected = current;
        center(state, current, 0.8);
        state.fitted = true;
      } else {
        fit(state);
      }
    } else if (active && state.followsCurrent && run?.currentScene) {
      followCurrent(state, run.currentScene);
    }
    await updateRun(host, run);
    redraw(state);
  }

  function setFocused(host, id) {
    const state = host.talewrightMap;
    if (!state) return;
    state.focused = id;
    state.selected = id;
    state.highlighted = null;
    if (state.nodes?.has(id))
      center(state, id, Math.max(state.transform.k, 0.8));
    redraw(state);
  }

  function clearFocused(host, id) {
    const state = host.talewrightMap;
    if (state?.focused !== id) return;
    state.focused = null;
    redraw(state);
  }

  function highlight(host, line) {
    const state = host.talewrightMap;
    if (!state) return;
    state.highlighted = line;
    redraw(state);
  }

  function fitHost(host) {
    const state = host.talewrightMap;
    if (state) fit(state);
  }

  function setActive(host, active, scene) {
    const state = host.talewrightMap;
    if (!state) return;
    state.active = active;
    if (active && state.followsCurrent)
      followCurrent(state, scene || state.run?.currentScene);
    reportStatus(state, statusText(state, state.run))
      ?.catch(error => console.error("Could not update map status.", error));
  }

  return {
    render, updateRun, exportMap,
    select: (host, id) => select(host.talewrightMap, id),
    setFocused, clearFocused, highlight, fit: fitHost, setActive,
    focusCurrent: (host, scene) => focusCurrent(host.talewrightMap, scene),
  };
})();
