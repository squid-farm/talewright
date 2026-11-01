#!/usr/bin/env bash
set -euo pipefail

version=${1:?Version required}
rid=${2:?Runtime identifier required}
case "$rid" in linux-x64|linux-arm64|win-x64|win-arm64|osx-x64|osx-arm64) ;; *) echo "Unsupported runtime: $rid" >&2; exit 1 ;; esac
test "$(python3 .github/scripts/package_version.py --tag "v$version")" = "$version"
output="$PWD/artifacts/cli/$rid"
if [[ -e "$output" ]]; then
  echo "Use a fresh staging directory; remove the previous output at $output before packaging." >&2
  exit 1
fi
mkdir -p "$output"
dotnet publish tooling/cli/src/Talewright.Cli -c Release -r "$rid" --self-contained true \
  -p:PackAsTool=false -p:Version="$version" -o "$output"
dotnet publish tooling/debugger/src/Talewright.Debugger -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -o "$output/debug-host"
cp LICENSE "$output/LICENSE.txt"
cp docs/guide/cli-installation.md "$output/INSTALL.md"
cache=$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')
mkdir -p "$output/licenses"
cp tooling/vscode-talewright/tests/server-notices.txt "$output/licenses/server-dependencies.txt"
for source in \
  'tryphotino/photino.Blazor/4164eedbe5f7cb46ae83fbc3bd1ed8c459620ec7/LICENSE' \
  'tryphotino/photino.NET/4e8b40a3732d7bacedccc4c718865990c148823a/LICENSE' \
  'tryphotino/photino.Native/3ba4b937d6337b5c58344445db08a5a9a63f07c8/LICENSE' \
  'spectreconsole/spectre.console/bbbb5729dde27b58deee44f447a788eea46ee451/LICENSE.md' \
  'spectreconsole/spectre.console.cli/9c88a343b8ce622e9fda4e4334b2057d773d90c9/LICENSE.md'; do
  IFS=/ read -r owner repo revision file <<< "$source"
  curl --fail --silent --show-error --location --retry 3 \
    "https://raw.githubusercontent.com/$source" -o "$output/licenses/$repo.txt"
done
for package in "$cache"/microsoft.*/{9.0.1,10.0.13} "$cache/microsoft.netcore.app.runtime.$rid/"*; do
  if [[ -d "$package" ]]; then
    name=$(basename "$(dirname "$package")")-$(basename "$package")
    mkdir -p "$output/licenses/$name"
    find "$package" -maxdepth 1 -type f \( -iname 'license*' -o -iname '*notice*' -o -name '*.nuspec' \) \
      -exec cp '{}' "$output/licenses/$name/" \;
  fi
done
extension=
if [[ "$rid" == win-* ]]; then extension=.exe; fi
for file in "Talewright.Cli$extension" "debug-host/Talewright.Debugger$extension" \
  Talewright.Cli.deps.json Talewright.Cli.runtimeconfig.json \
  debug-host/Talewright.Debugger.runtimeconfig.json debug-host/debug-assets/index.html \
  debug-host/debug-assets/map/map.js debug-host/debug-assets/map/vendor/THIRD-PARTY-NOTICES.md \
  debug-host/debug-assets/map/vendor/d3-zoom.bundle.min.js \
  debug-host/debug-assets/map/vendor/elk-api.js debug-host/debug-assets/map/vendor/elk-worker.min.js \
  debug-host/debug-assets/map/vendor/licenses/elkjs.md \
  debug-host/debug-assets/map/vendor/licenses/d3-color.txt \
  debug-host/debug-assets/map/vendor/licenses/d3-dispatch.txt \
  debug-host/debug-assets/map/vendor/licenses/d3-drag.txt \
  debug-host/debug-assets/map/vendor/licenses/d3-ease.txt \
  debug-host/debug-assets/map/vendor/licenses/d3-interpolate.txt \
  debug-host/debug-assets/map/vendor/licenses/d3-selection.txt \
  debug-host/debug-assets/map/vendor/licenses/d3-timer.txt \
  debug-host/debug-assets/map/vendor/licenses/d3-transition.txt \
  debug-host/debug-assets/map/vendor/licenses/d3-zoom.txt \
  debug-assets/index.html publish-assets/standalone/web/App.razor \
  debug-assets/map/map.js debug-assets/map/vendor/THIRD-PARTY-NOTICES.md \
  debug-assets/map/vendor/d3-zoom.bundle.min.js \
  debug-assets/map/vendor/elk-api.js debug-assets/map/vendor/elk-worker.min.js \
  debug-assets/map/vendor/licenses/elkjs.md \
  debug-assets/map/vendor/licenses/d3-color.txt \
  debug-assets/map/vendor/licenses/d3-dispatch.txt \
  debug-assets/map/vendor/licenses/d3-drag.txt \
  debug-assets/map/vendor/licenses/d3-ease.txt \
  debug-assets/map/vendor/licenses/d3-interpolate.txt \
  debug-assets/map/vendor/licenses/d3-selection.txt \
  debug-assets/map/vendor/licenses/d3-timer.txt \
  debug-assets/map/vendor/licenses/d3-transition.txt \
  debug-assets/map/vendor/licenses/d3-zoom.txt \
  publish-assets/standalone/desktop/App.razor publish-assets/player/talewright.css; do
  test -s "$output/$file" || { echo "Missing CLI archive asset: $file" >&2; exit 1; }
done
python3 - "$output" <<'PY'
import json, pathlib, sys
root = pathlib.Path(sys.argv[1])
for file in [root / "Talewright.Cli.runtimeconfig.json", root / "debug-host/Talewright.Debugger.runtimeconfig.json"]:
    runtime = json.loads(file.read_text())["runtimeOptions"]
    assert "framework" not in runtime and "frameworks" not in runtime, f"{file}: requires external .NET"
    assert any(item["name"] == "Microsoft.NETCore.App" for item in runtime["includedFrameworks"]), file
PY
mkdir -p artifacts/cli-archives
name="talewright-cli-$version-$rid"
if [[ "$rid" == win-* ]]; then
  (cd "$output" && zip -q -r -FS "../../cli-archives/$name.zip" .)
  archive="artifacts/cli-archives/$name.zip"
  unzip -tq "$archive"
else
  test -x "$output/Talewright.Cli"
  test -x "$output/debug-host/Talewright.Debugger"
  archive="artifacts/cli-archives/$name.tar.gz"
  tar -czf "$archive" -C "$output" .
  tar -tzf "$archive" >/dev/null
fi
(cd artifacts/cli-archives && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")
