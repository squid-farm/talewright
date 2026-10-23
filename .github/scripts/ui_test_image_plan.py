#!/usr/bin/env python3
"""Decide whether the UI test image needs to be built and published.

The image tag in image.json is the source of truth. A tag is published once:
- missing tag: build, verify, and (on main) publish it;
- existing tag with an unchanged image definition: nothing to do;
- existing tag with a changed image definition: fail, because the revision must be bumped.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTEXT = ".github/docker/ui-tests"
REFERENCE_PATTERN = re.compile(r"ghcr\.io/(?P<repository>squid-farm/talewright-ui-tests):(?P<tag>pw\d+\.\d+\.\d+-r\d+)")
ZERO_SHA = "0" * 40
MANIFEST_TYPES = ", ".join([
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.docker.distribution.manifest.v2+json",
])


def image_reference(root: Path = ROOT) -> str:
    reference = json.loads((root / CONTEXT / "image.json").read_text())["reference"]
    if not REFERENCE_PATTERN.fullmatch(reference):
        raise SystemExit(f"Invalid image reference: {reference}")
    return reference


def tag_exists(reference: str) -> bool:
    match = REFERENCE_PATTERN.fullmatch(reference)
    repository, tag = match["repository"], match["tag"]
    token_url = f"https://ghcr.io/token?scope=repository:{repository}:pull"
    with urllib.request.urlopen(token_url, timeout=30) as response:
        token = json.load(response)["token"]
    request = urllib.request.Request(
        f"https://ghcr.io/v2/{repository}/manifests/{tag}",
        method="HEAD",
        headers={"Authorization": f"Bearer {token}", "Accept": MANIFEST_TYPES},
    )
    try:
        with urllib.request.urlopen(request, timeout=30):
            return True
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return False
        raise SystemExit(f"Cannot establish whether {reference} exists: HTTP {error.code}") from error


def context_changed(base: str, head: str, root: Path = ROOT) -> bool:
    if not base or base == ZERO_SHA:
        return True
    result = subprocess.run(
        ["git", "diff", "--quiet", f"{base}...{head}", "--", CONTEXT],
        cwd=root, capture_output=True, text=True,
    )
    if result.returncode not in (0, 1):
        raise SystemExit(f"Cannot diff {base}..{head}: {result.stderr.strip()}")
    return result.returncode == 1


def decide(event: str, ref: str, changed: bool, exists: bool, reference: str) -> dict[str, object]:
    if exists and changed and event != "workflow_dispatch":
        raise SystemExit(
            f"{CONTEXT} changed but {reference} is already published. "
            "Bump the revision in image.json (pw<playwright>-r<revision>) so the new image gets a new tag.")
    build = not exists
    publish = build and event in ("push", "workflow_dispatch") and ref == "refs/heads/main"
    if exists:
        reason = f"{reference} is already published and the image definition is unchanged"
    elif publish:
        reason = f"{reference} is not published yet; publishing"
    else:
        reason = f"{reference} is not published yet; validating candidate only"
    return {"reference": reference, "build": build, "publish": publish, "reason": reason}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--event", required=True)
    parser.add_argument("--ref", required=True)
    parser.add_argument("--base", default="")
    parser.add_argument("--head", default="HEAD")
    args = parser.parse_args(argv)

    reference = image_reference()
    changed = args.event == "workflow_dispatch" or context_changed(args.base, args.head)
    plan = decide(args.event, args.ref, changed, tag_exists(reference), reference)
    print(plan["reason"])
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a") as handle:
            handle.write(f"reference={plan['reference']}\n")
            handle.write(f"build={str(plan['build']).lower()}\n")
            handle.write(f"publish={str(plan['publish']).lower()}\n")
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a") as handle:
            handle.write(f"{plan['reason']}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
