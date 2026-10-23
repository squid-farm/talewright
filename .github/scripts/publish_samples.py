#!/usr/bin/env python3
"""Publish with a Pages base path without permanently changing the local shell."""

import argparse
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def normalize_base_path(path):
    if not re.fullmatch(r"(?:/[A-Za-z0-9_.-]+)*/?", path):
        raise ValueError("Pages base path must be empty, /, or an absolute URL path.")
    if any(part in (".", "..") for part in path.split("/")):
        raise ValueError("Pages base path cannot contain relative segments.")
    return "/" + path.strip("/") + "/" if path.strip("/") else "/"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-path", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        base = normalize_base_path(args.base_path)
    except ValueError as error:
        parser.error(str(error))
    shell = ROOT / "apps/web/wwwroot/index.html"
    original = shell.read_text()
    marker = '<base href="/" />'
    if original.count(marker) != 1:
        parser.error("Expected exactly one root base element in the web shell.")
    try:
        shell.write_text(original.replace(marker, f'<base href="{base}" />'))
        subprocess.run(["dotnet", "publish", "apps/web", "-c", "Release",
                        "-o", str(args.output.resolve())], cwd=ROOT, check=True)
    finally:
        shell.write_text(original)


if __name__ == "__main__":
    main()
