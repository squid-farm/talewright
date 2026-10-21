# Testing the engine and player

## Fast .NET tests

```bash
dotnet test
```

The root solution includes compiler, runtime, rules, narrative, and core tests. It does **not** discover UI
tests or require downloaded browsers. `Talewright.E2E.slnx` is a separate, opt-in solution in this directory.

## One test-authoring surface

All player E2E cases and assertions live in **C#/xUnit** in `Talewright.Player.E2E.Tests`.
Shared behavior tests use `IPlayerDriver`: Playwright drives Chromium, Firefox, and WebKit, while a
thin Python AT-SPI adapter drives the actual Linux Photino window. The adapter contains native UI
actions, observations, process cleanup, and evidence capture only: no test discovery, story scenarios,
expected values, assertions, or separate test-report format.

Use the same xUnit assertions, fixture definitions, and `dotnet test --filter` syntax for both hosts.
DOM/CSS/media checks are explicitly marked `Host=Browser`; CLI/host checks are marked `Host=Linux`.
The runner excludes the other host's cases and combines that scope with your filter. Shared cases have
neither host trait and run on both. Native execution initializes neither Playwright nor a web server.
Browser observations read the DOM in one synchronous snapshot so an upload or library transition cannot
remove controls between a presence check and a text read. A driver regression exercises rapid replacements.

## CI and deployment checks

The Build workflow calls the reusable Tests workflow once per run. Tests retains
unit, Chromium, Firefox, WebKit, and real Linux Photino jobs on Ubuntu 24.04.
Each job uploads its reports/evidence even on failure. Both Build (including
package verification) and Tests must pass before any publishing job.

Test projects retain their original project references and suite boundaries.
The `src/Talewright/Talewright.csproj` packaging shell bundles the six headless
assemblies without moving their source or merging their projects. The engine package has no Blazor
dependency. CI packs the `Talewright` and `Talewright.Player` libraries and the `Talewright.Cli` tool, then restores
and builds isolated engine-only and Razor consumers using a fresh package cache.
This checks real package references, the Player's transitive engine dependency,
and its CSS/JavaScript assets, not just project-reference builds. CI also packs and installs
the separate `Talewright.Cli` tool, running JSON validation and story/standalone
publication outside the checkout.

```bash
python3 -m unittest discover -s tests -p 'test_ci.py' -v
dotnet build Talewright.slnx -c Release -p:Version=0.1.0-preview.1
dotnet pack Talewright.slnx -c Release --no-build -p:Version=0.1.0-preview.1 -o artifacts/packages
python3 .github/scripts/verify_packages.py artifacts/packages 0.1.0-preview.1 --smoke
```

Use a fresh output directory when changing package versions: verification rejects
unexpected/duplicate packages instead of accidentally publishing stale files.

Pages publishing also checks the real static output under the configured base
path. To reproduce a project-subpath deployment after installing Chromium:

```bash
python3 tests/run_e2e.py install-browsers --browser chromium
python3 .github/scripts/publish_samples.py --base-path /talewright --output artifacts/pages
BROWSER=chromium TALEWRIGHT_WEB_ROOT="$PWD/artifacts/pages/wwwroot" \
  TALEWRIGHT_WEB_BASE_PATH=/talewright TALEWRIGHT_TEST_RESULTS="$PWD/artifacts/test-results/pages" \
  dotnet test tests/Talewright.Player.E2E.Tests -c Release --no-build \
  --filter FullyQualifiedName~Bundled_sample_assets_work_when_served_at_the_selected_base_path
```

The fixture serves only the selected path, rejecting root requests outside it.
The check opens a bundled sample, verifies Player styling, and activates a choice;
resource/console errors fail it. No sample-specific plot text or route is asserted.
Without `TALEWRIGHT_WEB_BASE_PATH`, ordinary root-hosted browser tests are unchanged.

## Browser player tests

Requires the .NET 10 SDK and Python 3. No Node/npm project or PowerShell installation is needed.

```bash
# Once, and again when the pinned Microsoft.Playwright package changes.
# --with-deps may require sudo to install browser system libraries.
python3 tests/run_e2e.py install-browsers

# Publishes the real web host in Release, serves it on an owned loopback port,
# and runs the same functional cases in Chromium, Firefox, and WebKit.
python3 tests/run_e2e.py web

python3 tests/run_e2e.py web --browser firefox
python3 tests/run_e2e.py web --browser chromium --filter FullyQualifiedName~StoryLoadingTests
```

The helper uses a temporary publish directory and removes it after the run. The .NET suite owns its Kestrel
static server and browser contexts, waits for readiness, and disposes them without touching other servers.
There are no application test switches and no fake web host.

To run directly against an existing published output:

```bash
dotnet publish src/Talewright.Host.Web -c Release -o artifacts/web
TALEWRIGHT_WEB_ROOT="$PWD/artifacts/web/wwwroot" BROWSER=webkit \
  dotnet test tests/Talewright.Player.E2E.Tests --filter 'Host!=Linux'
```

Use the helper for the normal path; direct invocation requires the matching browser binaries to be installed.

### Standalone publisher artifacts

CI also publishes a selected story with no development samples and tests the
extracted ZIP. Standalone cases are explicitly skipped in normal development-host
runs; opt in with `TALEWRIGHT_STANDALONE=1` and `Artifact=Standalone`.
These checks verify auto-start, title, play, save/load across a host restart,
history, and (in browsers) a one-story library, real styling, JS and network errors.

```bash
dotnet run --project src/Talewright.Cli -c Release -- \
  publish samples/LostRelic -a --target web --output artifacts/game-web.zip
unzip artifacts/game-web.zip -d artifacts/game-web
TALEWRIGHT_STANDALONE=1 TALEWRIGHT_WEB_ROOT="$PWD/artifacts/game-web" \
  TALEWRIGHT_WEB_BASE_PATH=/games/published BROWSER=chromium \
  dotnet test tests/Talewright.Player.E2E.Tests -c Release --filter Artifact=Standalone

dotnet run --project src/Talewright.Cli -c Release -- \
  publish samples/LostRelic -a --target desktop --output artifacts/game-desktop.zip
unzip artifacts/game-desktop.zip -d artifacts/game-desktop
TALEWRIGHT_STANDALONE=1 TALEWRIGHT_DESKTOP_EXECUTABLE="$PWD/artifacts/game-desktop/start.sh" \
  python3 tests/run_e2e.py linux --filter Artifact=Standalone
```

The second command publishes all six OS/CPU builds; the native test launches the
universal ZIP's actual launcher and selected self-contained Linux binary.
`--runtime linux-x64` instead creates a flat single-runtime archive; point
`TALEWRIGHT_DESKTOP_EXECUTABLE` at its `Talewright.Host.Desktop` binary.
`TALEWRIGHT_PUBLISHED_TITLE` overrides the expected title for another story.
Windows/macOS/ARM64 build verification is not a substitute for runtime testing.

Each case records a screenshot, DOM, trace, and browser-error list under
`artifacts/test-results/<browser>/<case-id>/`. The xUnit output associates the case with that directory.
Evidence is retained for successful cases too, making a later visual comparison possible without rerunning.
The helper also writes one TRX report per browser. Open traces with the matching Playwright trace viewer
locally; they can contain story text and should not be uploaded to public trace-viewing services.

Console errors, uncaught JavaScript exceptions, failed requests, HTTP error responses, and the Blazor error
banner fail a test. Expected bad-story errors must appear in the player UI, not in an ignored console log.

## Real Linux desktop tests

This suite launches **Photino itself** and drives the installed WebKitGTK accessibility tree, not a browser
substitute. The same C#/xUnit project uses the distro's Python AT-SPI bindings as a command adapter;
dogtail and Selenium are not needed.

The current target is the Linux development machine: Ubuntu 24.04, distro WebKitGTK 4.1, .NET 10,
system Python 3, GTK 3, Xvfb, and AT-SPI. Windows is tested manually; cross-platform native automation
is not a prerequisite. GitHub Actions integration is separate from this test-authoring retrofit.
The distro's security-updated WebKitGTK is intentionally not frozen at a vulnerable package patch version;
the runner prints the installed versions for diagnosis.

```bash
sudo apt-get update
sudo apt-get install -y libwebkit2gtk-4.1-0 libnotify4 libgtk-3-0t64 \
  xvfb xauth dbus-x11 at-spi2-core libatk-adaptor python3-pyatspi python3-gi gir1.2-gtk-3.0 xfwm4 wmctrl

# Run as your ordinary user, not root.
python3 tests/run_e2e.py linux --probe
python3 tests/run_e2e.py linux
python3 tests/run_e2e.py linux --filter FullyQualifiedName~PlayerBehaviorTests
python3 tests/run_e2e.py linux --filter 'FullyQualifiedName~Explicit_story_scenario'
```

The runner builds the production desktop host and the same C# test project, and starts
a private Xvfb/D-Bus/accessibility session and Xfwm4 window manager, with isolated XDG
configuration/cache/data/runtime directories.
It uses `/usr/bin/python3` so a Python virtualenv does not hide the distro's GI/AT-SPI bindings.
Each case generates its own ZIP bytes or CLI directory directly from the C# fixtures, starts a fresh
Photino process through a JSON-lines adapter, and removes its temporary story files on disposal.
Tests locate controls by accessible roles/names and activate actions or actual keyboard events; no OCR or
coordinate-based clicking is used. Archive selection invokes the GTK location entry's own accessible
activation action (rather than sending Enter to the globally focused window) and waits
for the chooser to close; accepting an AT-SPI action alone does not mean GTK opened the selected file.
All native cases run serially. App logs, accessibility-tree dumps,
screenshots, and adapter logs are retained under `artifacts/test-results/linux/<case-id>/`.
Both hosts produce xUnit results in TRX: `artifacts/test-results/linux.trx` for the native run.
The xUnit output associates each result with its evidence directory. Old Python unittest filter names
are no longer supported; use the same .NET filter syntax shown above.

The feasibility probe checks bundled-story opening, choice activation, latest-passage focus, character-sheet
exposure, and the real native file chooser. Missing prerequisites, inaccessible controls, or unmatched
native test filters fail explicitly. Neither the native runner nor the browser suite silently skips a
missing engine. The WebKit sandbox is not disabled.
CI runs this probe before the full native suite so a broken chooser fails once instead of timing out
separately in every upload-dependent scenario.

## Stable, programmatically supplied stories

`Talewright.TestStories` contains named C# raw-string YAML and `.tale` definitions, rather than checked-in
loose archives/story directories. The existing compiler already accepts a dictionary of logical filenames
and contents:

```csharp
var source = new StorySource(new Dictionary<string, string>
{
    ["story.yaml"] = yaml,
    ["chapters/main.tale"] = tale,
});
var result = StoryCompiler.Compile(source);
```

Shared helpers produce that dictionary, in-memory ZIP bytes, or a temporary folder. Browser tests upload
the ZIP through the normal file input; native tests open the same bytes/folders using CLI arguments or the
native chooser; runtime tests compile the source directly. Product assemblies never reference this project.

`PlayerScenario` describes a short sequence of user choices with **explicit**, independently written expected
passage text, choices, sheet content, and terminal state. One C# test executes those steps against either
driver. Host-specific cases and all expectations stay in that same xUnit project rather than extending
this into a general testing DSL or introducing Python tests.

To inspect generated files deliberately:

```bash
dotnet run --project tests/Talewright.TestStories -- /tmp/my-talewright-test-stories
```

That command leaves the requested directory for inspection; the normal E2E helpers create and clean their
own temporary files directly. Do not commit generated files. For deterministic checks, prefer `randomness: off`
and deliberate DCs. A dedicated seeded fixture tests replay; no test retries until a preferred roll occurs.

## Coverage and boundaries

| Behavior | Runtime/compiler | Three browser engines | Actual Linux Photino |
| --- | --- | --- | --- |
| Explicit branching, gates, hubs/once/priority, nested conditional arms | Yes | Shared scenarios | Same shared scenarios |
| Choice/entry checks, player/NPC, success/failure, effects | Yes | Shared scenarios | Same shared scenarios |
| Items, modifiers, quantities, faction triggers, progression | Yes | Shared scenarios and exact sheet assertions | Shared visible-state assertions |
| Transcript history, restart/replay, library/resume/replacement | Engine state only | Yes | Yes |
| Strong/emphasis/code/line breaks, speech, non-ASCII, inert HTML/media placeholders | Compiler/renderer | DOM assertions | Rendered text scenarios; not DOM-style assertions |
| Multi-file/root/wrapped ZIPs and errors/recovery | Yes | Yes | Generated fixtures and native chooser/CLI |
| 20 MiB upload acceptance/rejection | N/A | Exact boundary and +1 byte | Same boundary and invalid uploads |
| ZIP 1,000-file / 5 MiB-file / 50 MiB-total limits | Exact boundaries and rejection | Visible rejection/recovery | Visible rejection/recovery |
| Keyboard, latest-frame focus, long-transcript scroll | N/A | Yes | Yes |
| Responsive layout | N/A | Narrow and breakpoint-adjacent viewports | Actual native window resize |
| Light/dark palette and reduced-motion preference | N/A | Browser media emulation | Not automatically asserted; OS-specific preference integration remains a native manual check |
| CLI auto-start, multiple paths, missing/bad paths, Unicode/spaces | Source loading only | N/A | Yes |
| Shipped sample integration | Compilation only | Bundled open/first frame only | Bundled open/first frame only; probe interactions use fixtures |

The sample smoke guards the real authored files and embedded-resource packaging. Detailed assertions do
not depend on its plot, wording, branch count, or route to an ending. The old sample-specific scripted and
random-playthrough assertions are replaced by deterministic fixture assertions; focused existing unit tests
remain independent.

The suite does not claim every possible story-path permutation, exact visual pixel equivalence, screen-reader
speech correctness, or coverage of unimplemented saves, character creation, images, or settings. Only library
states reachable in the production hosts are included: there is no test host to fabricate empty/loading/failing
library implementations.

## Other desktop platforms

- **Windows / WebView2 (manual for now):** Playwright supports attachment through CDP. A future Windows fixture can launch
  Photino with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<owned-port>` and an isolated
  `WEBVIEW2_USER_DATA_FOLDER`, then use `Chromium.ConnectOverCDPAsync`. Verify Photino startup/readiness,
  profile isolation, native chooser coverage, and process cleanup on Windows before claiming this coverage.
  Never enable remote debugging in the normal application.
- **Linux / WebKitWebDriver:** viable only after integrating WebKitGTK's automation-enabled context and
  session/view-creation hooks in the native host. Installing the driver alone does not automate stock Photino.
  This uses the WebDriver protocol with a client such as Selenium, not Playwright's WebKit protocol.
  Playwright has no supported direct attachment to the embedded WebKitGTK view; that is not a limitation
  on automating the Linux app itself. The current AT-SPI suite already does that with distro accessibility
  dependencies and needs no native fork or remote-debugging endpoint.
- **macOS / WKWebView:** Playwright cannot directly attach to an arbitrary WKWebView. Investigate native
  accessibility/XCTest automation on a macOS runner. SafariDriver drives Safari, not the Photino window.
- **Playwright WebKit is not Safari or the installed system webview.** It provides valuable engine coverage
  but cannot replace these native integration checks.

References: [Playwright .NET runners](https://playwright.dev/dotnet/docs/test-runners),
[WebView2/CDP](https://playwright.dev/docs/webview2),
[WebKit versions](https://playwright.dev/docs/browsers#webkit),
[WebKitGTK automation context](https://webkitgtk.org/reference/webkit2gtk/stable/method.WebContext.set_automation_allowed.html),
[automation-controlled views](https://webkitgtk.org/reference/webkit2gtk/stable/property.WebView.is-controlled-by-automation.html).
