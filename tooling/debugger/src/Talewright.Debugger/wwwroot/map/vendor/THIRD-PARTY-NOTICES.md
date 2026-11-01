# Story map dependencies

The Author Debugger bundles these libraries locally so the Map tab works without network access:

- **elkjs 0.12.1** lays out the map in a Web Worker. Its Eclipse Public License 2.0 is in
  `licenses/elkjs.md`.
- **d3-zoom 3.0.0** provides pan and zoom. The bundle also includes d3-color, d3-dispatch, d3-drag,
  d3-ease, d3-interpolate, d3-selection, d3-timer and d3-transition. Their license notices are in
  the corresponding `licenses/d3-*.txt` files.

These notices ship beside the bundled files under `debug-assets/map/vendor`.
