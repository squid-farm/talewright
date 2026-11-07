#!/usr/bin/env python3
"""Publish with a Pages base path without permanently changing the local shell."""

import argparse
from datetime import datetime, timezone
from pathlib import Path
import re
import subprocess

from package_version import version_from_tag

ROOT = Path(__file__).resolve().parents[2]


def normalize_base_path(path):
    if not re.fullmatch(r"(?:/[A-Za-z0-9_.-]+)*/?", path):
        raise ValueError("Pages base path must be empty, /, or an absolute URL path.")
    if any(part in (".", "..") for part in path.split("/")):
        raise ValueError("Pages base path cannot contain relative segments.")
    return "/" + path.strip("/") + "/" if path.strip("/") else "/"


def publish_command(output, version=None, now=None):
    """Pin one build date so the player and version.json report the same identity."""
    date = (now or datetime.now(timezone.utc)).strftime("%Y-%m-%d")
    command = ["dotnet", "publish", "apps/web", "-c", "Release", "-o", str(Path(output).resolve()),
               f"-p:TalewrightBuildDate={date}"]
    if version is not None:
        command.append(f"-p:Version={version}")
    return command


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-path", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--version", help="Release version from package_version.py; omitted builds are 'dev'")
    args = parser.parse_args()
    try:
        base = normalize_base_path(args.base_path)
        if args.version is not None:
            version_from_tag("v" + args.version)
    except ValueError as error:
        parser.error(str(error))
    shell = ROOT / "apps/web/wwwroot/index.html"
    original = shell.read_text()
    marker = '<base href="/" />'
    if original.count(marker) != 1:
        parser.error("Expected exactly one root base element in the web shell.")
    try:
        shell.write_text(original.replace(marker, f'<base href="{base}" />'))
        subprocess.run(publish_command(args.output, args.version), cwd=ROOT, check=True)
    finally:
        shell.write_text(original)


if __name__ == "__main__":
    main()
