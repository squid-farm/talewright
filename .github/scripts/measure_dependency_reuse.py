"""Compare dependency stores in fresh checkouts; never reuse build or Nx task outputs."""

import argparse
import datetime
import io
import json
import os
from pathlib import Path
import platform
import shutil
import statistics
import subprocess
import tarfile
import time

ROOT = Path(__file__).resolve().parents[2]
PROJECT = "engine/tests/Talewright.Talemark.Tests/Talewright.Talemark.Tests.csproj"
EDITOR = "tooling/vscode-talewright"
CANDIDATES = {
    "nx": ([["./nx", "--version"]], []),
    "editor": (
        [["npm", "ci", "--prefix", EDITOR]],
        [["npm", "test", "--prefix", EDITOR]],
    ),
    "nuget": (
        [["dotnet", "restore", PROJECT, "--force", "--no-http-cache"]],
        [["dotnet", "test", PROJECT, "--no-restore", "--configuration", "Release",
          "--filter", "FullyQualifiedName!~EditorSnippetTests&FullyQualifiedName!~EditorManifestSchemaTests"]],
    ),
}


def summarize(records):
    """Keep unsuccessful trials visible and exclude them from paired savings."""
    pairs = {}
    for record in records:
        pairs.setdefault(record["trial"], {})[record["condition"]] = record
    deltas = []
    for pair in pairs.values():
        if set(pair) == {"cold", "warm"} and all(r["success"] for r in pair.values()):
            deltas.append(pair["cold"]["install_seconds"] - pair["warm"]["install_seconds"])
    return {
        "successful_pairs": len(deltas),
        "failed_records": sum(not r["success"] for r in records),
        "paired_install_savings_seconds": deltas,
        "median_install_savings_seconds": statistics.median(deltas) if deltas else None,
        "minimum_install_savings_seconds": min(deltas) if deltas else None,
        "maximum_install_savings_seconds": max(deltas) if deltas else None,
    }


def timed_command(command, cwd, environment, log):
    start = time.monotonic()
    with log.open("wb") as stream:
        result = subprocess.run(command, cwd=cwd, env=environment, stdout=stream,
                                stderr=subprocess.STDOUT, check=False)
    return {"command": command, "seconds": time.monotonic() - start,
            "returncode": result.returncode, "log": str(log)}


def store_size(path):
    return sum(p.stat().st_size for p in path.rglob("*") if p.is_file())


def measure(candidate, trials, output, snapshot):
    records = []
    for trial in range(1, trials + 1):
        pair = output / f"{candidate}-{trial}"
        pair.mkdir()
        store = pair / "store"
        store.mkdir()
        for condition in ("cold", "warm"):
            checkout = pair / condition
            checkout.mkdir()
            with tarfile.open(fileobj=io.BytesIO(snapshot)) as archive:
                archive.extractall(checkout, filter="data")
            environment = dict(os.environ, NX_DAEMON="false", NX_SKIP_NX_CACHE="true",
                               NX_WRAPPER_SKIP_INSTALL="", npm_config_cache=str(store),
                               NUGET_PACKAGES=str(store),
                               NUGET_HTTP_CACHE_PATH=str(pair / f"http-{condition}"),
                               DOTNET_CLI_TELEMETRY_OPTOUT="1")
            commands = []
            install, verification = CANDIDATES[candidate]
            for index, command in enumerate(install + verification):
                result = timed_command(command, checkout, environment,
                                       pair / f"{condition}-{index}.log")
                commands.append(result)
                if result["returncode"]:
                    break
            success = (len(commands) == len(install + verification)
                       and all(c["returncode"] == 0 for c in commands))
            record = {
                "trial": trial, "condition": condition, "success": success,
                "install_seconds": sum(c["seconds"] for c in commands[:len(install)]),
                "total_command_seconds": sum(c["seconds"] for c in commands),
                "store_bytes": store_size(store), "commands": commands,
            }
            records.append(record)
            (output / f"{candidate}.json").write_text(
                json.dumps({"records": records, "summary": summarize(records)}, indent=2) + "\n"
            )
            print(f"{candidate} trial={trial} {condition}: "
                  f"{record['install_seconds']:.3f}s install; success={success}", flush=True)
            shutil.rmtree(checkout)
            if not success:
                raise RuntimeError(f"Failed experiment: inspect {commands[-1]['log']}")
        start = time.monotonic()
        archive_path = shutil.make_archive(str(pair / "store-proxy"), "gztar", store)
        save_seconds = time.monotonic() - start
        restored = pair / "restored-proxy"
        restored.mkdir()
        start = time.monotonic()
        with tarfile.open(archive_path) as archive:
            archive.extractall(restored, filter="data")
        overhead = {
            "trial": trial, "local_gzip_save_seconds": save_seconds,
            "local_gzip_restore_seconds": time.monotonic() - start,
            "archive_bytes": Path(archive_path).stat().st_size,
            "note": "Local gzip/filesystem proxy, NOT hosted Actions cache transfer overhead",
        }
        (pair / "overhead.json").write_text(json.dumps(overhead, indent=2) + "\n")
        shutil.rmtree(restored)
        Path(archive_path).unlink()
        shutil.rmtree(store)
    return summarize(records)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", choices=(*CANDIDATES, "all"), default="all")
    parser.add_argument("--trials", type=int, default=3)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.trials < 3:
        parser.error("At least three paired trials are required")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    snapshot = subprocess.check_output(["git", "archive", "HEAD"], cwd=ROOT)
    metadata = {
        "started_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "sha": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "platform": platform.platform(),
        "trials": args.trials,
        "candidate": args.candidate,
        "versions": {tool: subprocess.check_output([tool, "--version"], cwd=ROOT,
                                                 text=True).strip()
                     for tool in ("node", "npm", "dotnet", "python3")},
        "method": "Fresh HEAD checkout each condition; cold seeds warm per pair; "
                  "isolated stores and HTTP caches; OS page/DNS caches not controlled.",
    }
    (output / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")
    candidates = CANDIDATES if args.candidate == "all" else [args.candidate]
    for candidate in candidates:
        print(json.dumps({candidate: measure(candidate, args.trials, output, snapshot)}),
              flush=True)


if __name__ == "__main__":
    main()
