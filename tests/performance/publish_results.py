#!/usr/bin/env python3
"""Collect real-file performance measurements into a durable per-run history."""

import argparse
import csv
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def benchmark_rows(root):
    rows = []
    for path in sorted(root.rglob("*-report.csv")):
        with path.open(newline="", encoding="utf-8-sig") as source:
            for row in csv.DictReader(source):
                if not row.get("Method"):
                    continue
                rows.append({
                    "report": path.name,
                    "method": row.get("Method"),
                    "parameters": row.get("Params", ""),
                    "job": row.get("Job", ""),
                    "mean": row.get("Mean", ""),
                    "p95": row.get("P95", ""),
                    "allocated": row.get("Allocated", ""),
                })
    return rows


def integration_rows(root):
    rows = []
    for path in sorted(root.rglob("*.trx")):
        try:
            document = ET.parse(path)
        except ET.ParseError as error:
            raise ValueError(f"Could not parse test report {path}: {error}") from error
        for output in document.iter():
            if output.tag.rsplit("}", 1)[-1] != "StdOut" or not output.text:
                continue
            for line in output.text.splitlines():
                candidate = line.strip()
                if not candidate.startswith("{"):
                    continue
                try:
                    measurement = json.loads(candidate)
                except json.JSONDecodeError:
                    continue
                if isinstance(measurement, dict) and measurement.get("measurement"):
                    rows.append({"report": path.name, **measurement})
    return rows


def collect(input_dir):
    metadata = [read_json(path) for path in sorted(input_dir.rglob("metadata-*.json"))]
    publication_summaries = [
        read_json(path) for path in sorted(input_dir.rglob("publication-summary.json"))
    ]
    publications = [
        result
        for summary in publication_summaries
        for result in summary.get("results", [])
    ]
    return {
        "schemaVersion": 1,
        "runId": os.environ.get("GITHUB_RUN_ID", "local"),
        "runAttempt": int(os.environ.get("GITHUB_RUN_ATTEMPT", "1")),
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "commit": os.environ.get("GITHUB_SHA", ""),
        "branch": os.environ.get("GITHUB_REF_NAME", ""),
        "event": os.environ.get("GITHUB_EVENT_NAME", ""),
        "jobResults": {
            "benchmarksAndPublication": os.environ.get("PERF_BENCHMARKS_RESULT", "unknown"),
            "browserPlayer": os.environ.get("PERF_BROWSER_RESULT", "unknown"),
            "linuxPhotino": os.environ.get("PERF_PHOTINO_RESULT", "unknown"),
        },
        "workflowRunUrl": (
            f"{os.environ.get('GITHUB_SERVER_URL', 'https://github.com')}/"
            f"{os.environ.get('GITHUB_REPOSITORY', '')}/actions/runs/"
            f"{os.environ.get('GITHUB_RUN_ID', '')}"
        ),
        "metadata": metadata,
        "benchmarks": benchmark_rows(input_dir),
        "integrations": integration_rows(input_dir),
        "publications": publications,
    }


def markdown_for_run(run):
    lines = [
        f"# Real-file performance run {run['runId']}",
        "",
        f"- **Commit:** `{run['commit'] or 'unknown'}`",
        f"- **Branch:** `{run['branch'] or 'unknown'}`",
        f"- **Event:** {run['event'] or 'local'}",
        f"- **Run:** [{run['workflowRunUrl']}]({run['workflowRunUrl']})",
        f"- **Benchmark cases:** {len(run['benchmarks'])}",
        f"- **Player integration observations:** {len(run['integrations'])}",
        f"- **Publication workloads:** {len(run['publications'])}",
        f"- **Job outcomes:** benchmarks/publication `{run['jobResults']['benchmarksAndPublication']}`, "
        f"browser `{run['jobResults']['browserPlayer']}`, "
        f"Photino `{run['jobResults']['linuxPhotino']}`",
        "",
    ]
    if run["metadata"]:
        lines.extend([
            "## Runner and corpus metadata",
            "",
            "| Runtime | OS | CPU | Cores | Corpus | Power/cache note |",
            "|---|---|---|---:|---|---|",
        ])
        for metadata in run["metadata"]:
            corpus = metadata.get("corpus", {})
            lines.append(
                f"| {metadata.get('runtime', '')} | {metadata.get('os', '')} | "
                f"{metadata.get('cpuModel', '')} | {metadata.get('processorCount', '')} | "
                f"{corpus.get('Tier', corpus.get('tier', ''))} | "
                f"{metadata.get('powerAndCacheSettings', '')} |"
            )
        lines.append("")
    if run["benchmarks"]:
        lines.extend([
            "## BenchmarkDotNet",
            "",
            "| Method | Parameters | Mean | P95 | Allocated |",
            "|---|---|---:|---:|---:|",
        ])
        for row in run["benchmarks"]:
            lines.append(
                f"| {row['method']} | {row['parameters']} | {row['mean']} | "
                f"{row['p95']} | {row['allocated']} |"
            )
        lines.append("")
    if run["integrations"]:
        lines.extend([
            "## Player integration observations",
            "",
            "| Host | Browser | Corpus | Load/play (ms) | Choice (ms) | Save (ms) | Restore (ms) |",
            "|---|---|---|---:|---:|---:|---:|",
        ])
        for row in run["integrations"]:
            corpus = row.get("Metadata", row.get("metadata", {}))
            corpus_name = corpus.get("Tier", corpus.get("tier", ""))
            lines.append(
                f"| {row.get('host', '')} | {row.get('browser', '')} | {corpus_name} | "
                f"{row.get('uploadCompileFirstPlayableMs', row.get('coldApplicationOpenAndFirstPlayableMs', ''))} | "
                f"{row.get('choiceLatencyMs', '')} | {row.get('indexedDbSaveMs', '')} | "
                f"{row.get('indexedDbHistoryRestoreMs', '')} |"
            )
        lines.append("")
    if run["publications"]:
        lines.extend([
            "## Full publication workloads",
            "",
            "| Workload | Status | Elapsed (s) | Peak RSS (KiB) | Artifact bytes |",
            "|---|---|---:|---:|---:|",
        ])
        for row in run["publications"]:
            lines.append(
                f"| {row.get('name', '')} | {row.get('status', '')} | "
                f"{row.get('elapsedSeconds', '')} | {row.get('peakWorkingSetKiB', '')} | "
                f"{row.get('artifactBytes', '')} |"
            )
        lines.append("")
    if not run["benchmarks"] and not run["integrations"] and not run["publications"]:
        lines.extend(["No structured measurements were found in this run's artifacts.", ""])
    return "\n".join(lines)


def update_history(history_dir, run, report):
    runs_dir = history_dir / "runs"
    runs_dir.mkdir(parents=True, exist_ok=True)
    run_path = runs_dir / f"{run['runId']}-{run['runAttempt']}.json"
    run_path.write_text(json.dumps(run, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    reports_dir = history_dir / "reports"
    reports_dir.mkdir(parents=True, exist_ok=True)
    report_path = reports_dir / f"{run['runId']}-{run['runAttempt']}.md"
    report_path.write_text(report, encoding="utf-8")

    runs = [read_json(path) for path in sorted(runs_dir.glob("*.json"))]
    runs.sort(key=lambda value: (value.get("createdAt", ""), value.get("runId", "")))
    lines = [
        "# Real-file performance history",
        "",
        "Each row links to a run's workflow summary; machine-readable measurements are in `runs/`. "
        "Compare like-for-like workloads and review runner metadata before interpreting small changes.",
        "",
        "| Date (UTC) | Commit | Benchmarks | Player observations | Publications | Jobs | Run |",
        "|---|---|---:|---:|---:|---|---|",
    ]
    for entry in reversed(runs):
        lines.append(
            f"| {entry.get('createdAt', '')[:10]} | "
            f"`{entry.get('commit', '')[:12]}` | {len(entry.get('benchmarks', []))} | "
            f"{len(entry.get('integrations', []))} | {len(entry.get('publications', []))} | "
            f"{entry.get('jobResults', {}).get('benchmarksAndPublication', '')}/"
            f"{entry.get('jobResults', {}).get('browserPlayer', '')}/"
            f"{entry.get('jobResults', {}).get('linuxPhotino', '')} | "
            f"[{entry.get('runId', '')}]({entry.get('workflowRunUrl', '')}) · "
            f"[report](reports/{entry.get('runId', '')}-{entry.get('runAttempt', 1)}.md) · "
            f"[JSON](runs/{entry.get('runId', '')}-{entry.get('runAttempt', 1)}.json) |"
        )
    (history_dir / "index.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return run_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--history", required=True, type=Path)
    args = parser.parse_args()
    if not args.input.is_dir():
        parser.error(f"Input directory does not exist: {args.input}")

    run = collect(args.input)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "run.json").write_text(
        json.dumps(run, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    report = markdown_for_run(run)
    (args.output / "summary.md").write_text(report, encoding="utf-8")
    update_history(args.history, run, report)
    print(report, end="")


if __name__ == "__main__":
    main()
