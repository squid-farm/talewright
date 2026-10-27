#!/usr/bin/env python3
"""Resolve a supported release tag or an explicitly non-release CI version."""

import argparse
import json
import re


def version_from_tag(tag):
    match = re.fullmatch(
        r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
        r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?",
        tag,
    )
    if match is None:
        raise ValueError("Expected vMAJOR.MINOR.PATCH[-PRERELEASE]; build metadata is not supported.")
    prerelease = match[4]
    if prerelease and any(part.isdigit() and len(part) > 1 and part.startswith("0")
                          for part in prerelease.split(".")):
        raise ValueError("Numeric prerelease identifiers must not have leading zeros.")
    return tag[1:]


def release_identity(tag):
    """Reserve 1..998 for previews and 999 for stable in each patch block."""
    version = version_from_tag(tag)
    match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)(?:-preview\.([1-9][0-9]*))?", version)
    if match is None:
        raise ValueError("Public releases support only vMAJOR.MINOR.PATCH[-preview.N].")
    major, minor, patch = (int(match[index]) for index in (1, 2, 3))
    preview = int(match[4]) if match[4] else None
    if preview is not None and preview > 998:
        raise ValueError("Preview sequence must be between 1 and 998.")
    vscode_patch = patch * 1000 + (preview if preview is not None else 999)
    if max(major, minor, vscode_patch) > 2147483647:
        raise ValueError("Mapped Marketplace version components must fit signed 32-bit integers.")
    return {
        "nuget": version,
        "vscode": f"{major}.{minor}.{vscode_patch}",
        "prerelease": preview is not None,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--tag")
    source.add_argument("--ci", type=int, help="GitHub run number; never a publishing trigger")
    parser.add_argument("--release-json", action="store_true", help="Resolve public release identity")
    args = parser.parse_args()
    if args.release_json and args.tag is None:
        parser.error("--release-json requires --tag")
    if args.tag is not None:
        try:
            version = json.dumps(release_identity(args.tag)) if args.release_json else version_from_tag(args.tag)
        except ValueError as error:
            parser.error(str(error))
    else:
        if args.ci < 1:
            parser.error("CI run numbers must be positive.")
        version = f"0.0.0-ci.{args.ci}"
    print(version)


if __name__ == "__main__":
    main()
