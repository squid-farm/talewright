#!/usr/bin/env python3
"""Restore and persist the last successful hosted-player Pages snapshot."""

import argparse
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile
import time

from hosted_site import MAX_FILE_BYTES, MAX_FILES, MAX_SITE_BYTES, check_size, read_manifest

STATE_BRANCH = "hosted-player-state"
STATE_FILE = "state.json"


def command(arguments, cwd=None):
    result = subprocess.run(arguments, cwd=cwd, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(f"{arguments[0]} failed ({result.returncode}): {result.stderr.strip()}")
    return result.stdout


def github_json(endpoint):
    return json.loads(command(["gh", "api", endpoint]))


def deployments(repository):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid GitHub repository name.")
    return github_json(f"repos/{repository}/deployments?environment=github-pages&per_page=100")


def deployment_status(repository, deployment):
    statuses = github_json(f"repos/{repository}/deployments/{deployment['id']}/statuses")
    return statuses[0] if statuses else None


def run_from_status(repository, status):
    url = status.get("log_url") or ""
    match = re.fullmatch(
        rf"https://github\.com/{re.escape(repository)}/actions/runs/([0-9]+)/job/[0-9]+", url)
    return int(match[1]) if match else None


def previous_deployment(repository, current_run):
    for deployment in deployments(repository):
        status = deployment_status(repository, deployment)
        if status is None or run_from_status(repository, status) == current_run:
            continue
        if status["state"] in ("queued", "waiting", "pending", "in_progress"):
            raise RuntimeError("Another GitHub Pages deployment is in progress.")
        if status["state"] == "success":
            run = run_from_status(repository, status)
            if run is None:
                raise RuntimeError("The live Pages deployment has no associated Actions run.")
            return {"id": deployment["id"], "run": run}
    raise RuntimeError("No successful GitHub Pages deployment is available to preserve.")


def current_deployment_id(repository, run_id):
    for _ in range(30):
        matches = []
        for deployment in deployments(repository):
            status = deployment_status(repository, deployment)
            if status is not None and run_from_status(repository, status) == run_id:
                matches.append(deployment["id"])
        if len(matches) == 1:
            return matches[0]
        if len(matches) > 1:
            raise RuntimeError("Multiple Pages deployments belong to the current Actions run.")
        time.sleep(1)
    raise RuntimeError("The current GitHub Pages deployment was not found.")


def extract_tar(archive, destination):
    destination = Path(destination)
    if destination.exists():
        raise ValueError(f"Snapshot extraction destination already exists: {destination}")
    destination.mkdir(parents=True)
    names = set()
    total = 0
    with tarfile.open(archive, "r:*") as source:
        for member in source:
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts or "\\" in member.name or ".git" in path.parts:
                raise ValueError(f"Unsafe snapshot path: {member.name}")
            if not path.parts:
                continue
            target = destination.joinpath(*path.parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            if not member.isfile() or path in names:
                raise ValueError(f"Unsupported or duplicate snapshot entry: {member.name}")
            names.add(path)
            if len(names) > MAX_FILES or member.size > MAX_FILE_BYTES:
                raise ValueError(f"Snapshot entry exceeds hosting limits: {member.name}")
            total += member.size
            if total > MAX_SITE_BYTES:
                raise ValueError("Snapshot exceeds the GitHub Pages size limit.")
            target.parent.mkdir(parents=True, exist_ok=True)
            stream = source.extractfile(member)
            if stream is None:
                raise ValueError(f"Cannot read snapshot entry: {member.name}")
            with stream, target.open("xb") as output:
                shutil.copyfileobj(stream, output)


def remote_state_oid(repository_path):
    ref = f"refs/heads/{STATE_BRANCH}"
    output = command(["git", "ls-remote", "--heads", "origin", ref], cwd=repository_path).strip()
    if not output:
        return None
    lines = output.splitlines()
    if len(lines) != 1:
        raise RuntimeError("Unexpected hosted-player state branch references.")
    oid, name = lines[0].split("\t")
    if name != ref or not re.fullmatch(r"[0-9a-f]{40}", oid):
        raise RuntimeError("Invalid hosted-player state branch reference.")
    return oid


def download_live_snapshot(repository, deployment, destination):
    with tempfile.TemporaryDirectory(prefix="hosted-bootstrap-") as temporary:
        command(["gh", "run", "download", str(deployment["run"]), "--repo", repository,
                 "--name", "github-pages", "--dir", temporary])
        archive = Path(temporary) / "artifact.tar"
        if not archive.is_file():
            raise RuntimeError("The live Pages artifact is unavailable; preserve it before retrying.")
        extract_tar(archive, destination)
    read_manifest(destination)


def restore(repository_path, repository, run_id, destination, information):
    repository_path, destination, information = map(Path, (repository_path, destination, information))
    if destination.exists() or information.exists():
        raise ValueError("Restore output paths must not already exist.")
    live = previous_deployment(repository, run_id)
    oid = remote_state_oid(repository_path)
    if oid is None:
        download_live_snapshot(repository, live, destination)
    else:
        command(["git", "fetch", "--no-tags", "--depth=1", "origin",
                 f"refs/heads/{STATE_BRANCH}"], cwd=repository_path)
        if command(["git", "rev-parse", "FETCH_HEAD"], cwd=repository_path).strip() != oid:
            raise RuntimeError("The hosted-player state branch changed while it was being restored.")
        with tempfile.TemporaryDirectory(prefix="hosted-state-") as temporary:
            archive = Path(temporary) / "state.tar"
            with archive.open("wb") as output:
                result = subprocess.run(["git", "archive", "FETCH_HEAD"], cwd=repository_path,
                                        stdout=output, stderr=subprocess.PIPE)
            if result.returncode:
                raise RuntimeError(f"git archive failed: {result.stderr.decode().strip()}")
            extracted = Path(temporary) / "state"
            extract_tar(archive, extracted)
            metadata = json.loads((extracted / STATE_FILE).read_text(encoding="utf-8"))
            if metadata.get("schema") != 1 or type(metadata.get("deployment_id")) is not int:
                raise ValueError("Invalid hosted-player state metadata.")
            site = extracted / "site"
            manifest = read_manifest(site)
            if metadata["deployment_id"] != live["id"]:
                if manifest["latest"] is not None or manifest["releases"] or manifest["dev"] is not None:
                    raise RuntimeError("Pages changed outside the retained-player deployment; reconcile the state first.")
                download_live_snapshot(repository, live, destination)
            else:
                shutil.copytree(site, destination)
    check_size(destination)
    information.write_text(json.dumps({
        "schema": 1, "base_oid": oid, "previous_deployment_id": live["id"],
    }, indent=2) + "\n", encoding="utf-8")


def persist(repository_path, site, information, deployment_id, run_id):
    repository_path, site, information = map(Path, (repository_path, site, information))
    check_size(site)
    read_manifest(site)
    state = json.loads(information.read_text(encoding="utf-8"))
    base_oid = state.get("base_oid")
    if (state.get("schema") != 1 or "base_oid" not in state or type(deployment_id) is not int
            or (base_oid is not None and (not isinstance(base_oid, str)
                                         or not re.fullmatch(r"[0-9a-f]{40}", base_oid)))):
        raise ValueError("Invalid hosted-player persistence metadata.")
    if remote_state_oid(repository_path) != base_oid:
        raise RuntimeError("The hosted-player state branch changed during deployment.")

    with tempfile.TemporaryDirectory(prefix="hosted-state-worktree-") as temporary:
        worktree = Path(temporary) / "state"
        if state["base_oid"] is None:
            command(["git", "worktree", "add", "--detach", str(worktree), "HEAD"], cwd=repository_path)
            base = None
        else:
            command(["git", "fetch", "--no-tags", "--depth=1", "origin",
                     f"refs/heads/{STATE_BRANCH}"], cwd=repository_path)
            if command(["git", "rev-parse", "FETCH_HEAD"], cwd=repository_path).strip() != base_oid:
                raise RuntimeError("The hosted-player state branch changed during deployment.")
            command(["git", "worktree", "add", "--detach", str(worktree), "FETCH_HEAD"],
                    cwd=repository_path)
            base = base_oid
        try:
            if base is None:
                command(["git", "switch", "--orphan", f"hosted-state-{run_id}"], cwd=worktree)
            target = worktree / "site"
            if target.is_symlink():
                raise ValueError("The hosted-player state site cannot be a symbolic link.")
            if target.exists():
                shutil.rmtree(target)
            shutil.copytree(site, target)
            (worktree / STATE_FILE).write_text(json.dumps({
                "schema": 1, "deployment_id": deployment_id,
            }, indent=2) + "\n", encoding="utf-8")
            command(["git", "add", "--all", "--force", "--", "site", STATE_FILE], cwd=worktree)
            command(["git", "-c", "user.name=github-actions[bot]",
                     "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com",
                     "commit", "-m", "hosting: record deployed player snapshot"], cwd=worktree)
            command(["git", "push", "origin", f"HEAD:refs/heads/{STATE_BRANCH}"], cwd=worktree)
        finally:
            command(["git", "worktree", "remove", "--force", str(worktree)], cwd=repository_path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    restore_parser = commands.add_parser("restore")
    restore_parser.add_argument("--repository", required=True)
    restore_parser.add_argument("--run-id", required=True, type=int)
    restore_parser.add_argument("--destination", required=True, type=Path)
    restore_parser.add_argument("--information", required=True, type=Path)
    extract_parser = commands.add_parser("extract")
    extract_parser.add_argument("--archive", required=True, type=Path)
    extract_parser.add_argument("--destination", required=True, type=Path)
    persist_parser = commands.add_parser("persist")
    persist_parser.add_argument("--repository", required=True)
    persist_parser.add_argument("--run-id", required=True, type=int)
    persist_parser.add_argument("--site", required=True, type=Path)
    persist_parser.add_argument("--information", required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.command == "restore":
            restore(Path.cwd(), args.repository, args.run_id, args.destination, args.information)
        elif args.command == "extract":
            extract_tar(args.archive, args.destination)
        else:
            deployment_id = current_deployment_id(args.repository, args.run_id)
            persist(Path.cwd(), args.site, args.information, deployment_id, args.run_id)
    except (ValueError, RuntimeError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()
