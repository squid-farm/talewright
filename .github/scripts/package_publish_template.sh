#!/usr/bin/env bash
set -euo pipefail

version=${1:?Version required}
target=${2:?Target required}
rid=${3:-}
case "$target" in
  web)
    test -z "$rid" || { echo "The web template has no runtime identifier." >&2; exit 1; }
    project=apps/web/Talewright.Host.Web.csproj
    ;;
  desktop)
    case "$rid" in linux-x64|linux-arm64|win-x64|win-arm64|osx-x64|osx-arm64) ;; *) echo "Unsupported runtime: $rid" >&2; exit 1 ;; esac
    project=apps/desktop/Talewright.Host.Desktop.csproj
    ;;
  *)
    echo "Unsupported target: $target" >&2
    exit 1
    ;;
esac

output_root=${TALEWRIGHT_PUBLISH_TEMPLATE_OUTPUT:-artifacts/publish-templates}
staging="$output_root/build-$target${rid:+-$rid}"
if [[ -e "$staging" ]]; then
  echo "Use a fresh staging directory; remove the previous output at $staging before packaging." >&2
  exit 1
fi

publish_args=(
  publish "$project" -c Release -p:Version="$version"
  -p:TalewrightStandalone=true -p:TalewrightIncludeSamples=false
  -p:TalewrightBuildCommit="${GITHUB_SHA:-unknown}"
  -p:TalewrightBuildDate="$(date -u +%F)"
  -o "$staging"
)
if [[ "$target" == desktop ]]; then
  publish_args+=(-r "$rid" --self-contained true)
fi
dotnet "${publish_args[@]}"

payload="$staging"
if [[ "$target" == web ]]; then
  payload="$staging/wwwroot"
  test -s "$payload/index.html" || { echo "Missing runner entry point: $payload/index.html" >&2; exit 1; }
else
  executable=Talewright.Host.Desktop
  [[ "$rid" == win-* ]] && executable+=.exe
  test -s "$payload/$executable" || { echo "Missing desktop executable: $payload/$executable" >&2; exit 1; }
  test -s "$payload/wwwroot/index.html" || { echo "Missing desktop web assets: $payload/wwwroot/index.html" >&2; exit 1; }
fi

name="talewright-publish-template-$target${rid:+-$rid}-$version.zip"
archive="$output_root/$name"
mkdir -p "$(dirname "$archive")"
python3 - "$payload" "$archive" "$version" "$target" "$rid" <<'PY'
import json
import pathlib
import stat
import sys
import zipfile

root = pathlib.Path(sys.argv[1])
archive = pathlib.Path(sys.argv[2])
version, target, rid = sys.argv[3:]
metadata = json.dumps({
    "version": version,
    "target": target,
    "runtime": rid or None,
}, separators=(",", ":")).encode()

with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as output:
    entry = zipfile.ZipInfo(".talewright-template.json")
    entry.create_system = 3
    entry.compress_type = zipfile.ZIP_DEFLATED
    entry.external_attr = (stat.S_IFREG | 0o644) << 16
    output.writestr(entry, metadata)
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise SystemExit(f"Runner template contains a symbolic link: {path}")
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        info = zipfile.ZipInfo(relative)
        info.create_system = 3
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = (stat.S_IFREG | stat.S_IMODE(path.stat().st_mode)) << 16
        with path.open("rb") as source, output.open(info, "w") as destination:
            while chunk := source.read(1024 * 1024):
                destination.write(chunk)
PY
(cd "$(dirname "$archive")" && sha256sum "$name" > "$name.sha256")
unzip -tq "$archive"
