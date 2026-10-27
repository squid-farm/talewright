#!/usr/bin/env python3
"""Resume a GitHub release without replacing existing assets or hiding API errors."""

import argparse
import json
import os
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen

from verify_release_vsix import verify_vsix

API = "https://api.github.com"


def request(method, url, payload=None, *, binary=False, missing_ok=False):
    headers = {
        "Authorization": f"Bearer {os.environ['GH_TOKEN']}",
        "Accept": "application/octet-stream" if binary and method == "GET" else "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    data = None
    if payload is not None:
        headers["Content-Type"] = "application/octet-stream" if binary else "application/json"
        data = payload if binary else json.dumps(payload).encode()
    try:
        with urlopen(Request(url, data=data, headers=headers, method=method), timeout=120) as response:
            body = response.read()
    except HTTPError as error:
        if missing_ok and error.code == 404:
            return None
        raise
    return body if binary else json.loads(body)


def publish(vsix, tag, sha, repository, additional_assets=()):
    identity = verify_vsix(vsix, tag)
    base = f"{API}/repos/{repository}"
    commit = request("GET", f"{base}/commits/{quote(tag, safe='')}")
    if commit["sha"] != sha:
        raise ValueError("Release tag no longer points at the validated commit")
    release = request("GET", f"{base}/releases/tags/{quote(tag, safe='')}", missing_ok=True)
    if release is None:
        generated = request("POST", f"{base}/releases/generate-notes", {
            "tag_name": tag, "target_commitish": sha,
        })
        body = (
            "## For authors\n\n"
            "Install the Linux x64 VS Code extension from the attached VSIX or the "
            "[Marketplace](https://marketplace.visualstudio.com/items?itemName=squid-farm.talewright)"
            + (" (select **Switch to Pre-Release Version**)" if identity["prerelease"] else "")
            + ". NuGet packages use version `" + identity["nuget"] + "`; the extension uses `"
            + identity["vscode"] + "`.\n\n"
            "GitHub's **Source code (zip)** and **Source code (tar.gz)** downloads below are "
            "the tagged source. Self-contained CLI archives and checksums are attached separately; "
            "player binaries are not included. See "
            f"[getting started](https://github.com/{repository}/blob/{tag}/docs/guide/getting-started.md).\n\n"
            "**Before 1.0, story syntax, APIs and save formats may change without compatibility "
            "guarantees. Back up stories and saves before upgrading.** Review the changes below "
            "for migration details; maintainers should add any specific save-breaking changes.\n\n"
            + generated["body"]
        )
        release = request("POST", f"{base}/releases", {
            "tag_name": tag, "target_commitish": sha, "name": tag, "body": body,
            "draft": True, "prerelease": identity["prerelease"], "make_latest": "false",
        })
    if release["tag_name"] != tag or release["prerelease"] != identity["prerelease"]:
        raise ValueError("Existing release tag or pre-release channel differs; inspect it manually")
    for path, name in [(Path(vsix), "talewright-linux-x64.vsix"),
                       *((Path(path), Path(path).name) for path in additional_assets)]:
        assets = [asset for asset in release["assets"] if asset["name"] == name]
        contents = path.read_bytes()
        if assets:
            if len(assets) != 1 or request("GET", assets[0]["url"], binary=True) != contents:
                raise ValueError("Existing release asset differs; never overwrite a published version")
            print(f"Identical {name} already attached; keeping it.")
        else:
            upload = release["upload_url"].split("{", 1)[0]
            request("POST", f"{upload}?name={quote(name)}", contents, binary=True)
            print(f"Attached {name}.")
    if release["draft"]:
        request("PATCH", f"{base}/releases/{release['id']}", {
            "draft": False, "prerelease": identity["prerelease"], "make_latest": "false",
        })
        print(f"Published {tag}.")
    else:
        print(f"{tag} already published; keeping existing release notes.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("vsix", type=Path)
    parser.add_argument("--assets", nargs="+", type=Path, default=[])
    args = parser.parse_args()
    if os.environ.get("GITHUB_EVENT_NAME") != "push" or os.environ.get("GITHUB_REF_TYPE") != "tag":
        parser.error("GitHub releases are only created by a tag push")
    publish(args.vsix, os.environ["GITHUB_REF_NAME"], os.environ["GITHUB_SHA"],
            os.environ["GITHUB_REPOSITORY"], args.assets)


if __name__ == "__main__":
    main()
