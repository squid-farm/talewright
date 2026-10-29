#!/usr/bin/env python3
"""Collect real-file performance measurements into a durable per-run history."""

import argparse
import csv
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
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
                    "parameters": "; ".join(
                        f"{name}={row[name]}"
                        for name in ("TierName", "CorpusCase", "Boundary", "Params")
                        if row.get(name)
                    ),
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
        for result in document.iter():
            if result.tag.rsplit("}", 1)[-1] != "UnitTestResult":
                continue
            measured = False
            for output in result.iter():
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
                        measured = True
                        rows.append({
                            **measurement,
                            "report": path.name,
                            "testOutcome": result.get("outcome", "Unknown"),
                            "testName": result.get("testName", ""),
                        })
            name = result.get("testName", "")
            outcome = result.get("outcome", "Unknown")
            if not measured and "RealCorpus" in name and outcome != "Passed":
                tier = re.search(r'tierName:\s*"([^"]+)"', name)
                error = "\n".join(
                    element.text for element in result.iter()
                    if element.tag.rsplit("}", 1)[-1] == "Message" and element.text
                )
                rows.append({
                    "report": path.name,
                    "testName": name,
                    "testOutcome": outcome,
                    "host": "Linux Photino" if "Native" in name else "browser",
                    "browser": path.stem if "Native" not in name else "",
                    "Metadata": {"Tier": tier.group(1) if tier else (
                        "Low-compressibility" if "Low_compressibility" in name else "unknown"
                    )},
                    "error": error or "No error message was recorded in the test result.",
                })
    return rows


def collect(input_dir):
    metadata = [read_json(path) for path in sorted(input_dir.rglob("metadata-*.json"))]
    publications = []
    summaries = sorted(input_dir.rglob("publication-summary.json"))
    for path in summaries:
        publications.extend(read_json(path).get("results", []))
    summary_directories = {path.parent for path in summaries}
    for path in sorted(input_dir.rglob("publication-*.json")):
        if path.name != "publication-summary.json" and path.parent not in summary_directories:
            publications.append(read_json(path))
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
        f"- **Run:** [{run['workflowRunUrl']}]({run['workflowRunUrl']})"
        if run["workflowRunUrl"] else "- **Run:** local execution",
        f"- **Benchmark cases:** {len(run['benchmarks'])}",
        f"- **Player integration observations:** {len(run['integrations'])}",
        f"- **Publication workloads:** {len(run['publications'])}",
        f"- **Job outcomes:** benchmarks/publication `{run['jobResults']['benchmarksAndPublication']}`, "
        f"browser `{run['jobResults']['browserPlayer']}`, "
        f"Photino `{run['jobResults']['linuxPhotino']}`",
        "",
    ]
    if run.get("notes"):
        lines.extend([*run["notes"], ""])
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
            "| Host | Browser | Corpus | Test outcome | Load/play (ms) | Choice (ms) | Save (ms) | Restore (ms) |",
            "|---|---|---|---|---:|---:|---:|---:|",
        ])
        for row in run["integrations"]:
            corpus = row.get("Metadata", row.get("metadata", {}))
            corpus_name = corpus.get("Tier", corpus.get("tier", ""))
            lines.append(
                f"| {row.get('host', '')} | {row.get('browser', '')} | {corpus_name} | "
                f"{row.get('testOutcome', 'Unknown')} | "
                f"{row.get('uploadCompileFirstPlayableMs', row.get('coldApplicationOpenAndFirstPlayableMs', ''))} | "
                f"{row.get('choiceLatencyMs', '')} | {row.get('indexedDbSaveMs', '')} | "
                f"{row.get('indexedDbHistoryRestoreMs', '')} |"
            )
        lines.append("")
    failures = [row for row in run["integrations"] if row.get("error")]
    if failures:
        lines.extend(["### Player failures without measurements", ""])
        for row in failures:
            lines.extend([
                f"- **{row['testOutcome']}**: {row['testName']}",
                "",
                "    " + row["error"].replace("\n", "\n    "),
                "",
            ])
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

    runs = [(path, read_json(path)) for path in sorted(runs_dir.glob("*.json"))]
    current = []
    archived = []
    for path, entry in runs:
        if isinstance(entry, dict) and type(entry.get("schemaVersion")) is int and entry["schemaVersion"] == 1 and {
            "createdAt", "runId", "runAttempt", "commit", "workflowRunUrl",
            "benchmarks", "integrations", "publications", "jobResults",
        }.issubset(entry) and all(
            isinstance(entry[field], str) for field in ("createdAt", "runId", "commit", "workflowRunUrl")
        ) and type(entry["runAttempt"]) is int and all(
            isinstance(entry[field], list) for field in ("benchmarks", "integrations", "publications")
        ) and isinstance(entry["jobResults"], dict) and {
            "benchmarksAndPublication", "browserPlayer", "linuxPhotino",
        }.issubset(entry["jobResults"]) and all(
            isinstance(entry["jobResults"][field], str)
            for field in ("benchmarksAndPublication", "browserPlayer", "linuxPhotino")
        ):
            current.append((path, entry))
        else:
            archived.append(path)
    current.sort(key=lambda record: (record[1]["createdAt"], record[1]["runId"]))
    lines = [
        "# Real-file performance history",
        "",
        "Each row links to a run's workflow summary; machine-readable measurements are in `runs/`. "
        "Compare like-for-like workloads and review runner metadata before interpreting small changes.",
        "",
        "| Date (UTC) | Commit | Benchmarks | Player observations | Publications | Jobs | Run |",
        "|---|---|---:|---:|---:|---|---|",
    ]
    for path, entry in reversed(current):
        filename = path.stem
        run_link = (
            f"[{entry['runId']}]({entry['workflowRunUrl']})"
            if entry["workflowRunUrl"] else entry["runId"]
        )
        lines.append(
            f"| {entry['createdAt'][:10]} | "
            f"`{entry['commit'][:12]}` | "
            f"{len(entry['benchmarks'])} | "
            f"{len(entry['integrations'])} | {len(entry['publications'])} | "
            f"{entry['jobResults']['benchmarksAndPublication']}/"
            f"{entry['jobResults']['browserPlayer']}/"
            f"{entry['jobResults']['linuxPhotino']} | "
            f"{run_link} · "
            f"[report](reports/{filename}.md) · "
            f"[JSON](runs/{filename}.json) |"
        )
    if archived:
        lines.extend([
            "",
            "## Unsupported archived records",
            "",
            "These records do not match the current run shape and are not summarized or migrated. "
            "Their original JSON remains available.",
            "",
            *[f"- [{path.stem}](runs/{path.name})" for path in archived],
        ])
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
