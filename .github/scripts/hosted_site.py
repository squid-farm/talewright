#!/usr/bin/env python3
"""Prepare retained GitHub Pages snapshots without changing the current site."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import tempfile
import time
from urllib.error import URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

from package_version import release_identity

MANIFEST = "versions.json"
BUILD_IDENTITY = "hosted-build.json"
MAX_SITE_BYTES = 1_000_000_000
MAX_FILE_BYTES = 100_000_000
MAX_FILES = 100_000


def deployment_plan(event, ref, commit, rollback=""):
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("A full lowercase commit SHA is required.")
    if rollback:
        if event != "workflow_dispatch" or ref != "refs/heads/main":
            raise ValueError("A hosted-player rollback must be dispatched from main.")
        release_identity(rollback)
        return {"kind": "rollback", "label": rollback, "commit": commit,
                "base_path": f"/versions/{rollback}/"}
    if event != "push":
        raise ValueError("Hosted-player builds require a push to main or a public tag.")
    if ref == "refs/heads/main":
        return {"kind": "dev", "label": "dev", "commit": commit, "base_path": "/dev/"}
    if not ref.startswith("refs/tags/"):
        raise ValueError("Hosted-player builds require a push to main or a public tag.")
    tag = ref.removeprefix("refs/tags/")
    release_identity(tag)
    return {"kind": "release", "label": tag, "commit": commit,
            "base_path": f"/versions/{tag}/"}


def validate_plan(plan):
    if not isinstance(plan, dict):
        raise ValueError("Invalid hosted-player deployment plan.")
    kind, label, commit = plan.get("kind"), plan.get("label"), plan.get("commit")
    if not isinstance(label, str) or not isinstance(commit, str):
        raise ValueError("Invalid hosted-player deployment identity.")
    if kind == "release":
        expected = deployment_plan("push", "refs/tags/" + label, commit)
    elif kind == "dev":
        expected = deployment_plan("push", "refs/heads/main", commit)
    elif kind == "rollback":
        expected = deployment_plan("workflow_dispatch", "refs/heads/main", commit, label)
    else:
        raise ValueError(f"Unknown hosted-player deployment kind: {kind}")
    if plan != expected:
        raise ValueError("Hosted-player deployment plan does not match its identity.")


def tree_digest(directory):
    digest = hashlib.sha256()
    files = sorted(path for path in directory.rglob("*") if path.is_file())
    for path in directory.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"Symbolic links are not allowed in hosted snapshots: {path}")
    if not files:
        raise ValueError(f"Hosted snapshot has no files: {directory}")
    for path in files:
        relative = path.relative_to(directory).as_posix().encode("utf-8")
        digest.update(len(relative).to_bytes(8, "big"))
        digest.update(relative)
        digest.update(path.stat().st_size.to_bytes(8, "big"))
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
    return digest.hexdigest()


def check_size(site, limit=MAX_SITE_BYTES):
    if site.is_symlink() or not site.is_dir():
        raise ValueError(f"Hosted snapshot is not a directory: {site}")
    total = 0
    count = 0
    for path in site.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"Symbolic links are not allowed in hosted snapshots: {path}")
        if path.is_file():
            count += 1
            if count > MAX_FILES:
                raise ValueError(f"Hosted snapshot exceeds the {MAX_FILES}-file limit.")
            size = path.stat().st_size
            if size > MAX_FILE_BYTES:
                raise ValueError(f"Hosted file exceeds GitHub's file limit: {path}")
            total += size
    if total > limit:
        raise ValueError(f"Hosted site is {total} bytes; the Pages limit is {limit} bytes.")
    return total


def read_manifest(site):
    check_size(site)
    if not (site / "index.html").is_file():
        raise ValueError(f"Previous hosted snapshot has no root index.html: {site}")
    path = site / MANIFEST
    if not path.exists():
        if (site / "versions").exists() or (site / "dev").exists():
            raise ValueError("The previous site uses a reserved hosted-player path without a manifest.")
        return {"schema": 1, "latest": None, "releases": [], "dev": None}

    manifest = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(manifest, dict) or manifest.get("schema") != 1:
        raise ValueError("Unsupported hosted-player manifest.")
    releases = manifest.get("releases")
    if not isinstance(releases, list) or "latest" not in manifest or "dev" not in manifest:
        raise ValueError("Hosted-player manifest is missing required fields.")
    seen = set()
    for release in releases:
        if not isinstance(release, dict) or not isinstance(release.get("tag"), str):
            raise ValueError("Invalid hosted-player release entry.")
        tag = release["tag"]
        identity = release_identity(tag)
        if tag in seen or release.get("url") != f"/versions/{tag}/":
            raise ValueError(f"Duplicate or invalid hosted-player release: {tag}")
        seen.add(tag)
        if release.get("prerelease") is not identity["prerelease"]:
            raise ValueError(f"Incorrect prerelease identity for {tag}.")
        validate_record(site / "versions" / tag, release, tag)
    if manifest["latest"] is not None and manifest["latest"] not in seen:
        raise ValueError("The latest hosted-player release is not retained.")
    dev = manifest["dev"]
    if dev is not None:
        if not isinstance(dev, dict) or dev.get("url") != "/dev/":
            raise ValueError("Invalid hosted-player development entry.")
        validate_record(site / "dev", dev, "dev")
    elif (site / "dev").exists():
        raise ValueError("The development directory is not recorded in the manifest.")
    versions = site / "versions"
    if versions.exists():
        if not versions.is_dir() or {path.name for path in versions.iterdir()} != seen:
            raise ValueError("Retained version directories do not match the manifest.")
    elif seen:
        raise ValueError("Retained version directories are missing.")
    return manifest


def validate_record(directory, record, label):
    if not directory.is_dir():
        raise ValueError(f"Missing retained hosted player: {label}")
    if not re.fullmatch(r"[0-9a-f]{40}", record.get("commit", "")):
        raise ValueError(f"Invalid commit for retained hosted player: {label}")
    if not re.fullmatch(r"[0-9a-f]{64}", record.get("sha256", "")):
        raise ValueError(f"Invalid content digest for retained hosted player: {label}")
    if tree_digest(directory) != record["sha256"]:
        raise ValueError(f"Retained hosted player has changed: {label}")


def write_manifest(site, manifest):
    (site / MANIFEST).write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def set_root(site, tag):
    url = f"/versions/{tag}/"
    (site / "index.html").write_text(
        '<!doctype html>\n<html lang="en">\n<head>\n<meta charset="utf-8">\n'
        f'<meta http-equiv="refresh" content="0; url={url}">\n'
        "<title>Talewright</title>\n</head>\n<body>\n"
        f'<p>Opening Talewright {tag}. <a href="{url}">Continue</a>.</p>\n'
        "</body>\n</html>\n",
        encoding="utf-8",
    )
    for suffix in (".br", ".gz"):
        (site / ("index.html" + suffix)).unlink(missing_ok=True)


def make_payload(player, destination, plan):
    if not player.is_dir() or not (player / "index.html").is_file():
        raise ValueError(f"Published player is missing index.html: {player}")
    if (player / BUILD_IDENTITY).exists():
        raise ValueError(f"Published player already contains reserved {BUILD_IDENTITY}.")
    expected = f'<base href="{plan["base_path"]}" />'
    if (player / "index.html").read_text(encoding="utf-8").count(expected) != 1:
        raise ValueError(f"Published player must use base path {plan['base_path']}.")
    tree_digest(player)
    shutil.copytree(player, destination)
    if plan["kind"] == "dev":
        index = destination / "index.html"
        shell = index.read_text(encoding="utf-8")
        if shell.count("<body>") != 1:
            raise ValueError("Development player shell must contain exactly one body element.")
        banner = (
            '<div role="status" style="padding: .5rem 1rem; background: #59370c; '
            'color: #fff; font: 600 .875rem system-ui, sans-serif; text-align: center">'
            "Experimental development build</div>"
        )
        index.write_text(shell.replace("<body>", "<body>\n    " + banner), encoding="utf-8")
        for suffix in (".br", ".gz"):
            (destination / ("index.html" + suffix)).unlink(missing_ok=True)
    identity = {"schema": 1, "channel": plan["kind"], "label": plan["label"],
                "commit": plan["commit"]}
    (destination / BUILD_IDENTITY).write_text(json.dumps(identity, indent=2) + "\n", encoding="utf-8")
    return tree_digest(destination)


def prepare(previous, player, candidate, promoted, plan, limit=MAX_SITE_BYTES):
    previous, candidate, promoted = map(Path, (previous, candidate, promoted))
    validate_plan(plan)
    if candidate.exists() or promoted.exists():
        raise ValueError("Snapshot output directories must not already exist.")
    check_size(previous, limit)
    manifest = read_manifest(previous)

    if plan["kind"] == "rollback":
        if plan["label"] not in {release["tag"] for release in manifest["releases"]}:
            raise ValueError(f"Cannot select an unretained release: {plan['label']}")
        shutil.copytree(previous, promoted)
        manifest["latest"] = plan["label"]
        write_manifest(promoted, manifest)
        set_root(promoted, plan["label"])
        check_size(promoted, limit)
        return

    if player is None:
        raise ValueError("A published player is required for a release or development deployment.")
    with tempfile.TemporaryDirectory(prefix="hosted-player-") as temporary:
        payload = Path(temporary) / "player"
        digest = make_payload(Path(player), payload, plan)
        record = {"url": plan["base_path"], "commit": plan["commit"], "sha256": digest}
        shutil.copytree(previous, candidate)
        if plan["kind"] == "release":
            tag = plan["label"]
            identity = release_identity(tag)
            record.update({"tag": tag, "prerelease": identity["prerelease"]})
            previous_record = next((entry for entry in manifest["releases"] if entry["tag"] == tag), None)
            target = candidate / "versions" / tag
            if previous_record is not None:
                if previous_record != record or tree_digest(target) != digest:
                    raise ValueError(f"Refusing to overwrite retained version {tag} with different output.")
            elif target.exists():
                raise ValueError(f"Unrecorded version directory already exists: {tag}")
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copytree(payload, target)
            check_size(candidate, limit)
            shutil.copytree(candidate, promoted)
            if previous_record is None:
                manifest["releases"].append(record)
            manifest["latest"] = tag
            write_manifest(promoted, manifest)
            set_root(promoted, tag)
            check_size(promoted, limit)
        elif plan["kind"] == "dev":
            target = candidate / "dev"
            if target.exists():
                shutil.rmtree(target)
            shutil.copytree(payload, target)
            manifest["dev"] = record
            write_manifest(candidate, manifest)
            check_size(candidate, limit)
        else:
            raise ValueError(f"Unknown hosted-player deployment kind: {plan['kind']}")


def smoke(origin, site, plan, *, root=False, attempts=30, delay=2):
    validate_plan(plan)
    parsed = urlsplit(origin)
    if parsed.scheme not in ("http", "https") or not parsed.netloc or parsed.path not in ("", "/"):
        raise ValueError("Hosted-player smoke origin must be an HTTP(S) origin.")
    if plan["kind"] == "release" and not root:
        record = {"commit": plan["commit"]}
        channel = "release"
    else:
        manifest = read_manifest(Path(site))
        if plan["kind"] == "dev":
            record = manifest["dev"]
            channel = "dev"
        else:
            record = next((entry for entry in manifest["releases"] if entry["tag"] == plan["label"]), None)
            channel = "release"
    if record is None:
        raise ValueError("The smoke-test player is not recorded in the snapshot.")
    expected_identity = {"schema": 1, "channel": channel, "label": plan["label"],
                         "commit": record["commit"]}
    base = origin.rstrip("/") + plan["base_path"]
    last_error = None
    for attempt in range(attempts):
        try:
            def read(url):
                request = Request(f"{url}?hosted-check={time.time_ns()}",
                                  headers={"Cache-Control": "no-cache"})
                with urlopen(request, timeout=15) as response:
                    return response.read()

            index = read(base + "index.html").decode("utf-8")
            if f'<base href="{plan["base_path"]}" />' not in index:
                raise ValueError("The deployed player has the wrong base path.")
            if plan["kind"] == "dev" and "Experimental development build</div>" not in index:
                raise ValueError("The development player is missing its experimental label.")
            if json.loads(read(base + BUILD_IDENTITY)) != expected_identity:
                raise ValueError("The deployed player has the wrong build identity.")
            if not read(base + "_content/Talewright.Player/talewright.css"):
                raise ValueError("The deployed player stylesheet is empty.")
            if root:
                index = read(origin.rstrip("/") + "/index.html").decode("utf-8")
                if f'content="0; url={plan["base_path"]}"' not in index:
                    raise ValueError("The hosted root does not point to the selected release.")
            return
        except (URLError, TimeoutError, ValueError) as error:
            last_error = error
            if attempt + 1 < attempts:
                time.sleep(delay)
    raise RuntimeError(f"Hosted-player smoke test failed: {last_error}") from last_error


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    plan_parser = commands.add_parser("plan")
    plan_parser.add_argument("--event", required=True)
    plan_parser.add_argument("--ref", required=True)
    plan_parser.add_argument("--commit", required=True)
    plan_parser.add_argument("--rollback", default="")
    plan_parser.add_argument("--output", required=True, type=Path)
    prepare_parser = commands.add_parser("prepare")
    prepare_parser.add_argument("--previous", required=True, type=Path)
    prepare_parser.add_argument("--player", type=Path)
    prepare_parser.add_argument("--candidate", required=True, type=Path)
    prepare_parser.add_argument("--promoted", required=True, type=Path)
    prepare_parser.add_argument("--plan", required=True, type=Path)
    smoke_parser = commands.add_parser("smoke")
    smoke_parser.add_argument("--origin", required=True)
    smoke_parser.add_argument("--site", required=True, type=Path)
    smoke_parser.add_argument("--plan", required=True, type=Path)
    smoke_parser.add_argument("--root", action="store_true")
    args = parser.parse_args()
    try:
        if args.command == "plan":
            plan = deployment_plan(args.event, args.ref, args.commit, args.rollback)
            args.output.write_text(json.dumps(plan, indent=2) + "\n", encoding="utf-8")
            for key, value in plan.items():
                print(f"{key}={value}")
        elif args.command == "prepare":
            plan = json.loads(args.plan.read_text(encoding="utf-8"))
            prepare(args.previous, args.player, args.candidate, args.promoted, plan)
        else:
            plan = json.loads(args.plan.read_text(encoding="utf-8"))
            smoke(args.origin, args.site, plan, root=args.root)
    except (ValueError, RuntimeError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()
