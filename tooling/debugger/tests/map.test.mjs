import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { test } from 'node:test';

const source = await readFile(new URL('../src/Talewright.Debugger/wwwroot/map/map.js', import.meta.url), 'utf8');

function drawingContext() {
  const texts = [];
  return {
    texts,
    font: '10px sans-serif',
    fillStyle: '#000',
    strokeStyle: '#000',
    globalAlpha: 1,
    measureText(value) {
      const size = Number(this.font.match(/[\d.]+px/)[0].slice(0, -2));
      return { width: Array.from(value).length * size * 0.6 };
    },
    fillText(value, x, y) { texts.push({ value, x, y }); },
    setTransform() {},
    setLineDash() {},
    fillRect() {},
    beginPath() {},
    roundRect() {},
    fill() {},
    stroke() {},
    moveTo() {},
    lineTo() {},
    closePath() {},
    arc() {},
    translate() {},
  };
}

function transform(k = 1, x = 0, y = 0) {
  return {
    k, x, y,
    invert([px, py]) { return [(px - x) / k, (py - y) / k]; },
    translate(dx, dy) { return transform(k, x + dx, y + dy); },
    scale(scale) { return transform(k * scale, x, y); },
  };
}

async function renderMap(nodes) {
  const graphs = [];
  const canvases = [];
  class ELK {
    async layout(graph) {
      graphs.push(graph);
      let y = 20;
      const children = graph.children.map(child => {
        const placed = { ...child, x: 20, y };
        y += child.height + 30;
        return placed;
      });
      return { id: graph.id, children, edges: [], width: 360, height: y };
    }
  }

  const zoom = () => {
    const behavior = () => {};
    behavior.scaleExtent = () => behavior;
    behavior.on = (_, callback) => { behavior.onZoom = callback; return behavior; };
    behavior.transform = (_, value) => behavior.onZoom?.({ transform: value });
    return behavior;
  };
  const canvasContext = drawingContext();
  const canvas = {
    width: 2400, height: 2400, clientWidth: 2400, clientHeight: 2400,
    style: {}, getContext: () => canvasContext,
    addEventListener(name, callback) { this[`on${name}`] = callback; },
  };
  const host = { addEventListener() {} };
  const tooltip = {
    style: {}, hidden: true, offsetWidth: 180, offsetHeight: 40,
    replaceChildren(...children) { this.children = children; },
  };
  const dotnetCalls = [];
  const dotnet = {
    invokeMethodAsync(method, ...args) {
      dotnetCalls.push({ method, args });
      if (method === 'CheckPngExportSize') return Promise.resolve({ allowed: true, width: args[0], height: args[1] });
      return Promise.resolve();
    },
  };
  const document = {
    createElement(tag) {
      if (tag === 'canvas') {
        const context = drawingContext();
        const result = {
          width: 0, height: 0,
          getContext: () => context,
          toBlob(callback, type) { callback(new Blob(['png'], { type })); },
        };
        canvases.push({ element: result, context });
        return result;
      }
      return { textContent: '' };
    },
  };
  const selection = {
    call(callback, ...args) { callback(this, ...args); return this; },
    on() { return this; },
  };
  const identity = transform();
  const d3 = { zoom, zoomIdentity: identity, select: () => selection };
  const window = { devicePixelRatio: 1 };
  const sandbox = vm.createContext({
    window, ELK, d3, document,
    ResizeObserver: class { observe() {} },
    getComputedStyle: () => ({ color: 'rgb(0, 0, 0)' }),
    requestAnimationFrame: callback => callback(),
    DotNet: { createJSStreamReference: blob => blob },
    Blob,
  });
  vm.runInContext(source, sandbox);

  const view = { nodes, groups: [], lines: [], unreachableScenes: 0 };
  const map = window.talewrightMap;
  await map.render(host, canvas, tooltip, view, null, null, dotnet, false);
  return { map, host, canvas, tooltip, view, graphs, canvases, canvasContext, dotnetCalls, dotnet };
}

function scene(id, text = [id, 'A title', 'requires flag:ready']) {
  return {
    id, scene: id, area: 'main', text, name: `Scene ${id}`, group: null,
    start: false, end: false, hub: false, once: false, reachable: true, stub: false, items: [],
  };
}

function exportedTextEntries(svg) {
  return [...svg.matchAll(/<text x="([^"]+)" y="([^"]+)"[^>]*>(.*?)<\/text>/g)].map(([, x, y, value]) => ({
    x: Number(x), y: Number(y),
    value: value.replaceAll('&amp;', '&').replaceAll('&lt;', '<').replaceAll('&gt;', '>'),
  }));
}

async function exportSvg(rendered) {
  const saved = [];
  const save = rendered.dotnet.invokeMethodAsync.bind(rendered.dotnet);
  rendered.dotnet.invokeMethodAsync = (method, ...args) => {
    if (method === 'SaveMapExport')
      return args[1].text().then(text => saved.push({ format: args[0], text }));
    return save(method, ...args);
  };
  await rendered.map.exportMap(rendered.host, 'svg', rendered.dotnet, 0, 8192, 16_777_216);
  return saved[0].text;
}

test('scene widths use measured IDs while retaining the 200px minimum and 320px maximum', async () => {
  const longId = 'this.is.a.long.scene.identifier';
  const rendered = await renderMap([scene('short'), scene(longId)]);
  const sizes = new Map(rendered.graphs[0].children.map(node => [node.id, node]));

  assert.equal(sizes.get('short').width, 200);
  assert.ok(sizes.get(longId).width > 200 && sizes.get(longId).width < 320);

  const capped = await renderMap([scene('a'.repeat(100))]);
  assert.equal(capped.graphs[0].children[0].width, 320);
});

test('IDs wrap at dots, middle-ellipsize long segments, and collapse beyond four lines', async () => {
  const dotted = 'alpha12345678901234567890.beta12345678901234567890.gamma12345678901234567890';
  const overlong = 'abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ';
  const manySegments = Array.from({ length: 11 }, (_, i) => `segment${String(i).padStart(2, '0')}123456789`).join('.');
  const rendered = await renderMap([
    scene(dotted),
    scene(overlong),
    scene(manySegments),
  ]);
  const [dottedLines, overlongLines, collapsedLines] = [
    rendered.host.talewrightMap.laid.nodeText.get(dotted),
    rendered.host.talewrightMap.laid.nodeText.get(overlong),
    rendered.host.talewrightMap.laid.nodeText.get(manySegments),
  ];

  assert.deepEqual(Array.from(dottedLines.slice(0, 3)), [
    'alpha12345678901234567890',
    '.beta12345678901234567890',
    '.gamma12345678901234567890',
  ]);
  assert.equal(rendered.host.talewrightMap.laid.idLineCounts.get(manySegments), 4);
  assert.match(overlongLines[0], /^.{23}….{23}$/u);
  assert.ok(overlongLines[0].startsWith(overlong.slice(0, 23)));
  assert.ok(overlongLines[0].endsWith(overlong.slice(-23)));
  assert.equal(collapsedLines.length, 6);
  assert.deepEqual(Array.from(collapsedLines.slice(0, 4)), [
    'segment00123456789.segment01123456789',
    '.segment02123456789.segment03123456789',
    '…',
    '.segment10123456789',
  ]);

  const layout = rendered.host.talewrightMap.laid;
  const dottedSize = layout.boxes.get(dotted);
  assert.equal(dottedSize.height, 20 + (3 + 2) * 15);
  assert.equal(layout.boxes.get(manySegments).height, 20 + (4 + 2) * 15);
  assert.equal(rendered.view.nodes[0].text[0], dotted);
});

test('long stub IDs wrap without changing hover descriptions or selection targets', async () => {
  const target = 'chapter01.location02.a_very_long_target_scene_identifier';
  const stub = {
    id: `stub:${target}`, scene: null, area: 'unreachable', text: [`→ ${target}`],
    name: `Leads to ${target}`, group: null, start: false, end: false, hub: false,
    once: false, reachable: false, stub: true, items: [],
  };
  const shortStub = {
    id: 'stub:x', scene: null, area: 'unreachable', text: ['→ x'],
    name: 'Leads to x', group: null, start: false, end: false, hub: false,
    once: false, reachable: false, stub: true, items: [],
  };
  const rendered = await renderMap([scene(target), stub, scene('x'), shortStub]);
  const state = rendered.host.talewrightMap;
  const box = state.laid.boxes.get(stub.id);
  assert.equal(box.width, 320);
  assert.ok(state.laid.boxes.get(shortStub.id).width < 200);
  assert.ok(state.laid.nodeText.get(stub.id).length > 1);
  assert.ok(state.laid.idLineCounts.get(stub.id) <= 4);
  assert.equal(rendered.view.nodes.find(node => node.stub).name, `Leads to ${target}`);

  const screenX = state.transform.x + (box.x + box.width / 2) * state.transform.k;
  const screenY = state.transform.y + (box.y + box.height / 2) * state.transform.k;
  rendered.canvas.onclick({ offsetX: screenX, offsetY: screenY });
  assert.deepEqual(rendered.dotnetCalls.filter(call => call.method === 'SelectScene').at(-1).args, [target]);

  rendered.canvas.onmousemove({ offsetX: screenX, offsetY: screenY, buttons: 0 });
  assert.equal(rendered.tooltip.children[0].textContent, `Leads to ${target}`);
});

test('SVG and PNG render the same wrapped ID labels while preserving full node names', async () => {
  const id = 'alpha12345678901234567890.beta12345678901234567890.gamma12345678901234567890';
  const node = scene(id);
  node.start = true;
  node.hub = true;
  node.once = true;
  node.reachable = false;
  node.name = `The complete accessible name for ${id}`;
  const rendered = await renderMap([node]);
  const svg = await exportSvg(rendered);

  await rendered.map.exportMap(rendered.host, 'png', rendered.dotnet, 0, 8192, 16_777_216);
  const pngContext = rendered.canvases[0].context;
  const svgEntries = exportedTextEntries(svg);
  const expectedText = svgEntries.map(entry => entry.value);
  assert.deepEqual(pngContext.texts.map(entry => entry.value), expectedText);
  assert.deepEqual(rendered.canvasContext.texts.slice(-expectedText.length).map(entry => entry.value), expectedText);
  const idLineCount = rendered.host.talewrightMap.laid.idLineCounts.get(id);
  const idEntries = svgEntries.slice(0, idLineCount);
  const badge = svgEntries.find(entry => entry.value === 'START · HUB · ONCE · UNREACHABLE');
  const title = svgEntries.find(entry => entry.value === 'A title');
  const requirement = svgEntries.find(entry => entry.value === 'requires flag:ready');
  const box = rendered.host.talewrightMap.laid.boxes.get(id);

  assert.equal(idEntries.map(entry => entry.value).join(''), id);
  assert.ok(idEntries.every(entry => entry.x === box.x + 8));
  rendered.canvasContext.font = '10.5px monospace';
  assert.ok(idEntries.every(entry => rendered.canvasContext.measureText(entry.value).width <= box.width - 16));
  assert.ok(idEntries[0].y > badge.y);
  assert.equal(title.y, idEntries.at(-1).y + 15);
  assert.equal(requirement.y, title.y + 15);
  assert.equal(rendered.view.nodes[0].name, `The complete accessible name for ${id}`);
});
