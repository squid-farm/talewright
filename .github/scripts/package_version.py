#!/usr/bin/env python3
"""Resolve a supported release tag or an explicitly non-release CI version."""

import argparse
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--tag")
    source.add_argument("--ci", type=int, help="GitHub run number; never a publishing trigger")
    args = parser.parse_args()
    if args.tag is not None:
        try:
            version = version_from_tag(args.tag)
        except ValueError as error:
            parser.error(str(error))
    else:
        if args.ci < 1:
            parser.error("CI run numbers must be positive.")
        version = f"0.0.0-ci.{args.ci}"
    print(version)


if __name__ == "__main__":
    main()
