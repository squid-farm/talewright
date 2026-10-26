"""Route Nx-selected tasks to existing CI environments; never classify changed paths."""

import argparse
import html
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
TARGETS = [
    "test", "compiler-contract", "package-check", "editor-smoke",
    "e2e-web", "e2e-linux", "e2e-published-web", "e2e-published-linux",
    "ci-check", "check", "budget", "editor-full", "editor-contract", "build:release",
]
GROUPS = ("build", "unit", "browser", "linux-desktop", "editor", "checks")


def write_summary(content, *, stdout=False):
    if destination := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(destination, "a", encoding="utf-8") as stream:
            stream.write(content + "\n")
    elif stdout:
        print(content)


def code(value):
    return "`" + html.escape(str(value)).replace("`", "&#96;").replace(
        "|", "&#124;"
    ).replace("\n", " ").replace("\r", " ") + "`"


def selection_summary(plan, graph, args):
    fallback = any(task["target"]["project"] == "talewright-workspace"
                   for task in graph["tasks"]["tasks"].values())
    mode = "Full" if args.full else "Affected"
    lines = ["## Nx verification selection", "", f"Selection: **{mode}**."]
    if not args.full:
        lines.append(f"Comparison: {code(args.base)} to {code(args.head)}.")
    elif revision := args.head or os.environ.get("GITHUB_SHA"):
        lines.append(f"Revision: {code(revision)}.")
    if fallback and not args.full:
        lines.append("Conservative workspace fallback selected by the Nx graph.")
    if not any(plan.values()):
        lines.append("**No impact:** no verification tasks selected.")
    lines.extend(["", "| Environment | Selected projects and targets |", "|---|---|"])
    for group, tasks in plan.items():
        selected = ", ".join(code(f"{task['project']}:{task['target']}") for task in tasks)
        lines.append(f"| {group} | {selected or 'Not selected'} |")
    lines.extend(["", "Selection is graph-derived; execution timings appear in the work-job summaries."])
    return "\n".join(lines)


def execution_path(args):
    suffix = f"-{args.browser}" if args.group == "browser" else ""
    return args.plan.parent / f"execution-{args.group}{suffix}.json"


def save_execution(args, record):
    path = execution_path(args)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def execution_summary(args):
    lines = [f"## Nx execution: {args.group}" + (
        f" ({args.browser})" if args.group == "browser" else ""
    ), "", "Router batch timings include Nx prerequisites. They exclude job setup and queueing.",
        "Batches may run projects concurrently; these are not per-project timings or total workflow time.", ""]
    if not args.plan.exists():
        lines.append("**Incomplete:** selection plan unavailable; execution cannot be established.")
    else:
        tasks = json.loads(args.plan.read_text(encoding="utf-8"))[args.group]
        path = execution_path(args)
        record = json.loads(path.read_text(encoding="utf-8")) if path.exists() else None
        if record is None:
            lines.append("**Incomplete:** no execution record; the router may not have started.")
        else:
            lines.append(f"Router outcome: **{record['outcome']}**.")
        if not tasks:
            lines.append("No tasks selected. This does not authorize running an unselected group.")
        else:
            lines.extend(["", "| Target batch | Projects | Outcome | Elapsed seconds |",
                          "|---|---|---|---|"])
            for target in TARGETS:
                projects = [task["project"] for task in tasks if task["target"] == target]
                if not projects:
                    continue
                batch = next((batch for batch in record["batches"] if batch["target"] == target),
                             None) if record else None
                outcome = batch["outcome"] if batch else "incomplete (no record)"
                elapsed = batch.get("seconds") if batch else None
                duration = f"{elapsed:.2f}" if elapsed is not None else "Not available"
                if outcome in ("pending", "running"):
                    outcome = f"incomplete ({outcome})"
                lines.append(f"| {code(target)} | {', '.join(code(p) for p in projects)} "
                             f"| {outcome} | {duration} |")
    if url := os.environ.get("NX_CI_ARTIFACT_URL"):
        if not url.startswith("https://") or any(char in url for char in "\r\n()<> "):
            raise ValueError("Invalid uploaded artifact URL")
        lines.extend(["", f"[Uploaded artifact / reports]({url})"])
    write_summary("\n".join(lines), stdout=True)


def nx(*arguments, capture=False, environment=None):
    return subprocess.run(
        [str(ROOT / "nx"), *arguments], cwd=ROOT, check=True,
        env=dict(os.environ, NX_DAEMON="false", **(environment or {})),
        text=True, capture_output=capture,
    )


def task_group(project, target):
    if project == "talewright-workspace":
        return None
    if target == "build:release":
        return "checks"
    if project == "talewright-editor" or target == "compiler-contract":
        return "editor"
    if target == "test":
        # Opt-in UI tests must only run through the host-aware E2E runners.
        return None if project == "Talewright.Player.E2E.Tests" else "unit"
    if target == "package-check":
        return "build"
    if target in ("e2e-web", "e2e-published-web"):
        return "browser"
    if target in ("e2e-linux", "e2e-published-linux"):
        return "linux-desktop"
    if target in ("ci-check", "check", "budget"):
        return "checks"
    if target in TARGETS:
        raise ValueError(f"Unrouted verification target: {project}:{target}")
    return None  # Build prerequisites are executed by Nx in each selected job.


def make_plan(graph):
    groups = {group: [] for group in GROUPS}
    covered = set()

    def cover(project):
        if project in covered:
            return
        covered.add(project)
        for dependency in graph["graph"]["dependencies"].get(project, []):
            cover(dependency["target"])

    for task in graph["tasks"]["tasks"].values():
        project, target = task["target"]["project"], task["target"]["target"]
        if target == "build:release":
            continue
        group = task_group(project, target)
        if group:
            groups[group].append({"project": project, "target": target})
            cover(project)
    for task in graph["tasks"]["tasks"].values():
        project, target = task["target"]["project"], task["target"]["target"]
        if target == "build:release" and project != "talewright-workspace" and project not in covered:
            groups["checks"].append({"project": project, "target": target})
    for tasks in groups.values():
        tasks.sort(key=lambda task: (TARGETS.index(task["target"]), task["project"]))
    return groups


def select(args):
    command = ["run-many", "--all", "--exclude=talewright-workspace"] if args.full else [
        "affected", f"--base={args.base}", f"--head={args.head}",
    ]
    try:
        result = nx(*command, "-t", *TARGETS, "--dry-run", "--graph=stdout", capture=True)
        graph = json.loads(result.stdout)
    except (subprocess.CalledProcessError, json.JSONDecodeError):
        write_summary("## Nx verification selection\n\n"
                      "**Incomplete:** graph selection failed; no successful selection is reported.")
        raise
    plan = make_plan(graph)
    args.plan.parent.mkdir(parents=True, exist_ok=True)
    args.plan.write_text(json.dumps(plan, indent=2) + "\n", encoding="utf-8")
    selected = {group: bool(tasks) for group, tasks in plan.items()}
    print(json.dumps({"selected": selected, "tasks": plan}, indent=2))
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"selected={json.dumps(selected)}\n")
    write_summary(selection_summary(plan, graph, args))


def verify_unit_reports(expected):
    reports = unit_reports()
    if len(reports) != expected:
        raise ValueError(f"Expected {expected} unit reports; found {len(reports)}")
    for report in reports:
        counters = ET.parse(report).find(".//{*}Counters")
        if counters is None or int(counters.attrib["executed"]) == 0:
            raise ValueError(f"No tests executed: {report}")
        if int(counters.attrib["failed"]) != 0:
            raise ValueError(f"Failed tests: {report}")


def unit_reports():
    return [
        report
        for area in ("engine", "player", "tooling")
        for report in (ROOT / area).glob("**/artifacts/unit/*.trx")
    ]


def run_group(args):
    execution_path(args).unlink(missing_ok=True)
    plan = json.loads(args.plan.read_text(encoding="utf-8"))
    tasks = plan[args.group]
    if not tasks:
        raise ValueError(f"Attempted to run unselected group: {args.group}")
    for task in tasks:
        if task_group(task["project"], task["target"]) != args.group:
            raise ValueError(f"Misrouted task in {args.group}: {task}")
    if args.group == "unit":
        for report in unit_reports():
            report.unlink()
    record = {"outcome": "incomplete", "batches": [
        {"target": target, "projects": [
            task["project"] for task in tasks if task["target"] == target
        ], "outcome": "pending"}
        for target in TARGETS if any(task["target"] == target for task in tasks)
    ]}
    save_execution(args, record)
    try:
        for batch in record["batches"]:
            target = batch["target"]
            arguments = [
                "run-many", f"--projects={','.join(batch['projects'])}", f"--targets={target}",
                "--parallel=3", "--output-style=static",
            ]
            if target == "e2e-web":
                arguments.append(f"--configuration={args.browser}")
            batch["outcome"] = "running"
            save_execution(args, record)
            start = time.monotonic()
            try:
                nx(*arguments, environment={"BROWSER": args.browser})
            except subprocess.CalledProcessError:
                batch["outcome"] = "failure"
                raise
            else:
                batch["outcome"] = "success"
            finally:
                batch["seconds"] = time.monotonic() - start
                save_execution(args, record)
        if args.group == "unit":
            verify_unit_reports(len(tasks))
    except (subprocess.CalledProcessError, ValueError):
        record["outcome"] = "failure"
        raise
    else:
        record["outcome"] = "success"
    finally:
        save_execution(args, record)


def verify_results(needs, expected):
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        lines = ["## Nx job verification", "",
                 "Job outcomes are separate from router batch timings; missing or cancelled work is incomplete.",
                 "", "| Job | Expected | Actual |", "|---|---|---|"]
        for job in sorted(set(needs) | set(expected)):
            actual = needs.get(job, {}).get("result", "missing")
            if actual in ("missing", "cancelled") or (
                expected.get(job) is True and actual == "skipped"
            ):
                actual = f"incomplete ({actual})"
            lines.append(f"| {code(job)} | {code(expected.get(job, 'missing'))} | {code(actual)} |")
        write_summary("\n".join(lines))
    if set(needs) != set(expected):
        raise ValueError(f"Job result mismatch: expected {sorted(expected)}, got {sorted(needs)}")
    for job, selected in expected.items():
        if not isinstance(selected, bool):
            raise ValueError(f"Invalid selection for {job}: {selected!r}")
        wanted = "success" if selected else "skipped"
        actual = needs[job]["result"]
        if actual != wanted:
            raise ValueError(f"{job}: expected {wanted}, got {actual}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    selection = commands.add_parser("select")
    selection.add_argument("--full", action="store_true")
    selection.add_argument("--base")
    selection.add_argument("--head")
    selection.add_argument("--plan", type=Path, default=ROOT / "artifacts/ci/plan.json")
    run = commands.add_parser("run")
    run.add_argument("group", choices=GROUPS)
    run.add_argument("--plan", type=Path, default=ROOT / "artifacts/ci/plan.json")
    run.add_argument("--browser", choices=("chromium", "firefox", "webkit"), default="chromium")
    summary = commands.add_parser("summary")
    summary.add_argument("group", choices=GROUPS)
    summary.add_argument("--plan", type=Path, default=ROOT / "artifacts/ci/plan.json")
    summary.add_argument("--browser", choices=("chromium", "firefox", "webkit"), default="chromium")
    aggregate = commands.add_parser("aggregate")
    aggregate.add_argument("--needs", required=True)
    aggregate.add_argument("--expected", required=True)
    args = parser.parse_args()
    if args.command == "select":
        if not args.full and (not args.base or not args.head):
            parser.error("Affected selection requires both --base and --head")
        select(args)
    elif args.command == "run":
        run_group(args)
    elif args.command == "summary":
        execution_summary(args)
    else:
        verify_results(json.loads(args.needs), json.loads(args.expected))
        print("All selected verification jobs succeeded; unselected jobs were skipped.")


if __name__ == "__main__":
    main()
