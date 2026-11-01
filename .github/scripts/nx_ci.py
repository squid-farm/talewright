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
CHECK_TARGETS = ("ci-check", "check", "budget")
E2E_TARGETS = ("e2e-web", "e2e-linux", "e2e-published-web", "e2e-published-linux")
MAX_FAILURES_IN_SUMMARY = 20
MAX_ASSERTION_LENGTH = 400


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
    if not args.full:
        lines.append("Every workspace check task runs on pull requests; "
                     "other environments use affected selection.")
    if not any(plan.values()):
        lines.append("**No impact:** no verification tasks selected.")
    scopes = selection_scopes(args)
    lines.extend(["", "| Environment | Scope | Selected projects and targets |", "|---|---|---|"])
    for group, tasks in plan.items():
        selected = ", ".join(code(f"{task['project']}:{task['target']}") for task in tasks)
        lines.append(f"| {group} | {scopes[group].capitalize()} | {selected or 'Not selected'} |")
    lines.extend(["", "Selection is graph-derived; execution timings appear in the work-job summaries."])
    return "\n".join(lines)


def execution_path(args):
    suffix = f"-{args.browser}" if args.group == "browser" else ""
    return args.plan.parent / f"execution-{args.group}{suffix}.json"


def e2e_reports():
    return sorted((ROOT / "artifacts/test-results").glob("*.trx"))


def failure_summary(group):
    if group == "unit":
        reports = unit_reports()
    elif group in ("browser", "linux-desktop"):
        reports = e2e_reports()
    else:
        return []

    if not reports:
        return [
            "",
            "### Test failure details",
            "",
            "**No TRX report was produced.** The task may have failed before the test host "
            "started or before it could write results; treat this as a runner/infrastructure "
            "failure unless the task log shows otherwise.",
        ]

    failures = []
    report_notes = []
    executed_reports = 0
    for report in reports:
        try:
            root = ET.parse(report).getroot()
        except (OSError, ET.ParseError) as error:
            report_notes.append(
                f"- Could not read TRX report {code(report.relative_to(ROOT))}: "
                f"{code(error)}; this is a report/runner failure, not a test assertion."
            )
            continue

        counters = root.find(".//{*}Counters")
        results = root.findall(".//{*}UnitTestResult")
        failed_results = [
            result for result in results if result.attrib.get("outcome", "").lower() == "failed"
        ]
        if counters is not None:
            try:
                executed_count = int(counters.attrib.get("executed", "0"))
                failed_count = int(counters.attrib.get("failed", "0"))
            except ValueError as error:
                report_notes.append(
                    f"- TRX report {code(report.relative_to(ROOT))} has invalid counters: "
                    f"{code(error)}."
                )
                executed_count = failed_count = None
        else:
            executed_count = failed_count = None

        if executed_count is not None and executed_count > 0:
            executed_reports += 1
        elif not failed_results:
            report_notes.append(
                f"- TRX report {code(report.relative_to(ROOT))} contains no executed tests; "
                "this indicates a filter, test-host, or infrastructure failure."
            )

        for result in failed_results:
            message = result.find(".//{*}ErrorInfo/{*}Message")
            assertion = next(
                (line.strip() for line in (message.text or "").splitlines() if line.strip()),
                "",
            ) if message is not None else ""
            if len(assertion) > MAX_ASSERTION_LENGTH:
                assertion = assertion[:MAX_ASSERTION_LENGTH].rstrip() + "..."
            failures.append((
                result.attrib.get("testName") or "Unnamed test",
                assertion or "Assertion details are not available in the TRX report.",
                report.relative_to(ROOT),
            ))

        if failed_count is not None and failed_count > len(failed_results):
            report_notes.append(
                f"- TRX report {code(report.relative_to(ROOT))} records "
                f"{failed_count} failed test(s), but has no details for "
                f"{failed_count - len(failed_results)} of them."
            )

    lines = ["", "### Test failure details", ""]
    if failures:
        lines.extend(
            f"- {code(name)} — {code(assertion)} ({code(path)})"
            for name, assertion, path in failures[:MAX_FAILURES_IN_SUMMARY]
        )
        if len(failures) > MAX_FAILURES_IN_SUMMARY:
            lines.append(
                f"- {len(failures) - MAX_FAILURES_IN_SUMMARY} additional test failure(s) "
                "are in the uploaded TRX report."
            )
    elif executed_reports:
        lines.append(
            "No failed test cases were recorded in the available TRX reports. "
            "The task failed outside test assertions, likely during build, startup, or runner setup."
        )
    else:
        lines.append(
            "**No usable test results were recorded.** Treat this as a test-host, report, "
            "or infrastructure failure rather than a test assertion."
        )
    lines.extend(report_notes)
    return lines


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
            if record["outcome"] == "failure":
                lines.extend(failure_summary(args.group))
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


def selection_scopes(args):
    if args.full:
        return {group: "full" for group in GROUPS}
    return {group: "complete" if group == "checks" else "affected" for group in GROUPS}


def with_complete_checks(affected, checks):
    """Merge every workspace check task into an affected graph before routing."""
    merged = json.loads(json.dumps(affected))
    merged["tasks"]["tasks"].update(checks["tasks"]["tasks"])
    dependencies = merged["graph"]["dependencies"]
    for project, edges in checks["graph"]["dependencies"].items():
        known = dependencies.setdefault(project, [])
        known.extend(edge for edge in edges if edge not in known)
    return merged


def task_graph(*command, targets=TARGETS):
    result = nx(*command, "-t", *targets, "--dry-run", "--graph=stdout", capture=True)
    return json.loads(result.stdout)


def select(args):
    every_project = ("run-many", "--all", "--exclude=talewright-workspace")
    try:
        if args.full:
            graph = task_graph(*every_project)
        else:
            graph = with_complete_checks(
                task_graph("affected", f"--base={args.base}", f"--head={args.head}"),
                task_graph(*every_project, targets=CHECK_TARGETS),
            )
    except (subprocess.CalledProcessError, json.JSONDecodeError) as error:
        write_summary("## Nx verification selection\n\n"
                      "**Incomplete:** graph selection failed; no successful selection is reported.")
        if isinstance(error, subprocess.CalledProcessError):
            for output in (error.stdout, error.stderr):
                if output:
                    print(output, file=sys.stderr, end="")
        raise
    plan = make_plan(graph)
    args.plan.parent.mkdir(parents=True, exist_ok=True)
    args.plan.write_text(json.dumps(plan, indent=2) + "\n", encoding="utf-8")
    scopes = selection_scopes(args)
    args.plan.with_name("selection.json").write_text(
        json.dumps({"scopes": scopes}, indent=2) + "\n", encoding="utf-8",
    )
    selected = {group: bool(tasks) for group, tasks in plan.items()}
    print(json.dumps({"selected": selected, "scopes": scopes, "tasks": plan}, indent=2))
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
    elif args.group in ("browser", "linux-desktop"):
        for report in e2e_reports():
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
            output_style = "stream" if target in E2E_TARGETS else "static"
            arguments = [
                "run-many", f"--projects={','.join(batch['projects'])}", f"--targets={target}",
                "--parallel=3", f"--output-style={output_style}",
            ]
            if target == "e2e-web":
                arguments.append(f"--configuration={args.browser}")
            batch["outcome"] = "running"
            save_execution(args, record)
            start = time.monotonic()
            try:
                nx(*arguments, environment={"BROWSER": args.browser})
            except subprocess.CalledProcessError as error:
                batch["outcome"] = "failure"
                projects = ", ".join(batch["projects"])
                print(
                    f"Nx batch failed: {target} for {projects} "
                    f"(exit status {error.returncode}).",
                    file=sys.stderr,
                )
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
        try:
            run_group(args)
        except subprocess.CalledProcessError as error:
            raise SystemExit(error.returncode) from None
    elif args.command == "summary":
        execution_summary(args)
    else:
        verify_results(json.loads(args.needs), json.loads(args.expected))
        print("All selected verification jobs succeeded; unselected jobs were skipped.")


if __name__ == "__main__":
    main()
