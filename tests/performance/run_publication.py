#!/usr/bin/env python3
"""Run each full-publication workload with a timeout and a retained result row."""

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import time


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def parse_peak_rss(path):
    if not path.is_file():
        return None
    match = re.search(r"Maximum resident set size \(kbytes\):\s*(\d+)", path.read_text(errors="replace"))
    return int(match.group(1)) if match else None


def run_case(name, command, output, timeout_seconds):
    result_path = output / f"publication-{name}.json"
    timing_path = output / f"publication-{name}.time.txt"
    artifact = output / f"publish-{name}.zip"
    if artifact.exists():
        raise FileExistsError(f"Publication output already exists; use a fresh output directory: {artifact}")
    start = utc_now()
    started = time.monotonic()
    return_code = None
    try:
        process = subprocess.Popen(
            ["/usr/bin/time", "-v", "-o", str(timing_path), *command],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            start_new_session=True,
        )
        try:
            stdout, stderr = process.communicate(timeout=timeout_seconds)
            status = "succeeded" if process.returncode == 0 else "failed"
            return_code = process.returncode
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            stdout, stderr = process.communicate()
            status = "timed_out"
            return_code = process.returncode
    except OSError as error:
        stdout = ""
        stderr = f"{type(error).__name__}: {error}"
        status = "runner_failed"
        return_code = None

    row = {
        "name": name,
        "status": status,
        "startedAt": start,
        "completedAt": utc_now(),
        "elapsedSeconds": round(time.monotonic() - started, 3),
        "timeoutSeconds": timeout_seconds,
        "exitCode": return_code,
        "peakWorkingSetKiB": parse_peak_rss(timing_path),
        "artifactPath": str(artifact),
        "artifactBytes": artifact.stat().st_size if artifact.is_file() else None,
        "timingOutput": str(timing_path),
        "stdout": stdout,
        "stderr": stderr,
    }
    result_path.write_text(json.dumps(row, indent=2) + "\n")
    print(json.dumps({key: value for key, value in row.items() if key not in {"stdout", "stderr"}}), flush=True)
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--story", required=True, type=Path)
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--timeout-seconds", type=int, default=45 * 60)
    args = parser.parse_args()
    if args.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be positive")
    story = args.story.resolve(strict=True)
    cli = args.cli.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    workloads = [
        ("web", ["--all-in-one", "--target", "web"]),
        ("linux-x64", ["--all-in-one", "--target", "desktop", "--runtime", "linux-x64"]),
        ("universal-desktop", ["--all-in-one", "--target", "desktop"]),
    ]
    rows = []
    for name, options in workloads:
        command = [
            "dotnet",
            str(cli),
            "publish",
            str(story),
            *options,
            "--output",
            str(output / f"publish-{name}.zip"),
        ]
        rows.append(run_case(name, command, output, args.timeout_seconds))

    summary = {
        "generatedAt": utc_now(),
        "storyRoot": str(story),
        "cliAssembly": str(cli),
        "timeoutSecondsPerWorkload": args.timeout_seconds,
        "results": rows,
    }
    (output / "publication-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    return 0 if all(row["status"] == "succeeded" for row in rows) else 1


if __name__ == "__main__":
    raise SystemExit(main())
