# Browser playtesting without a desktop app

Use this procedure when you can create story files, run terminal commands and control a browser,
but cannot drive native windows. No Talewright source checkout or VS Code integration is needed.
If a required tool is unavailable, use the fallback below or report the limitation.

**Do not use `talewright run` or `talewright debug` for browser automation.** They open native
windows. There is currently no `talewright run --web` option, and `--non-interactive` does not
change that.

## 1. Check the version and validate

Run from the folder containing `story.talewright.yaml`:

```bash
talewright --version
talewright validate . --non-interactive --format json
```

Record the version. Fix compiler errors and review warnings before playing. Use documentation
matching the installed version: replace `main` in the brief's raw GitHub URL with the matching
release tag, keeping relative topic links on that version. Do not assume syntax on `main` exists
in an older release.

## 2. Publish a matching browser copy

This requires the .NET 10 SDK and package restore access. The publication uses the Player and
engine bundled with your installed CLI, rather than whichever version happens to be deployed online.

Choose fresh output paths outside the story folder. For example:

```bash
talewright publish . -a --target web --output ../my-story-playtest-web.zip --non-interactive
python3 -m zipfile -e ../my-story-playtest-web.zip ../my-story-playtest-web
python3 -m http.server 8000 --bind 127.0.0.1 --directory ../my-story-playtest-web
```

Run each command only after the previous one succeeds. The extraction and server examples require
Python 3; equivalent ZIP extraction and static HTTP hosting tools are fine. Keep the entire
extracted archive together, including `story/`. Serve only the publication directory, not your
workspace or home directory. If the port is occupied, choose a free port; do not stop another server.

Keep the server running and open **http://127.0.0.1:8000/** with your browser tool (adjust the port
if changed). Do not open `index.html` through `file://`. If the browser runs on another machine,
use your environment's approved port forwarding; do not expose the server publicly by default.

Confirm the story actually loads. A successful publish or HTTP response alone is not a playtest.

## 3. Play and inspect the rendered result

Use browser snapshots and visible controls rather than guessing element selectors or editing
internal player state.

- Start a fresh game and complete a route from the configured start to an ending. Confirm the
  intended scenes and choices appear.
- Exercise changed branches and their prerequisites. Check both outcomes of changed conditions,
  including hidden or unavailable choices. Cover success and failure when skill checks are
  involved; record outcomes you could not reach.
- Check that effects have the intended consequences: items are consumed or granted, flags unlock
  the right choices, and hubs do not accidentally strand the reader.
- Inspect speech presentation and speaker identity. Check rendered names, pronouns and verb
  agreement. When writing reusable character text, test relevant pronoun variants in a separate
  test copy without changing the author's chosen character settings.
- Check for blank choice labels, missing text, broken images, incorrect alternative text and
  unreadable layout. Inspect browser console errors and failed requests when your tools support them.
- For changes involving saves or history, exercise the affected save/load or Back/Forward
  behavior too.

After editing, validate and republish. Extract into a fresh directory, point the server at it and
reload the browser. Start a fresh game for source-validation checks; do not mistake an old loaded
story or saved session for the new build.

## 4. Fallback: use the hosted player

If local web publication is unavailable, make a story-only archive:

```bash
talewright publish . --output ../my-story-playtest.zip --non-interactive
```

Open **https://play.trytalewright.com/** and upload it using **Open a story file**, then
**Story archive**. Use only a browser environment approved for the story's contents.

Without the CLI, create a ZIP preserving the story's layout, with `story.talewright.yaml` at the
archive root, alongside its `.tale` files, declaration fragments and images. Do not include an
extra enclosing folder.

Record the hosted player's displayed version and commit. Hosted-player results are not proof
that the same story works on a different installed version. After edits, create and upload a
fresh archive.

## 5. Report evidence and limitations

Report the CLI/player version, validation result, routes and outcomes exercised, presentation
checks, and any untested branches or tool limitations. Distinguish **validated**, **published**,
and **played in a browser**. Never report a playtest you did not perform.

Stop your test server when finished. Remove only temporary outputs you created.
