"""Route Nx-selected tasks to existing CI environments; never classify changed paths.

Changed-path selection belongs to Nx inputs, not this environment router.
"""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
TARGETS = [
    "test", "compiler-contract", "package-check", "editor-smoke",
    "e2e-web", "e2e-linux", "e2e-published-web", "e2e-published-linux",
    "ci-check", "check", "budget", "editor-full", "editor-contract", "build:release",
]
GROUPS = ("build", "unit", "browser", "linux-desktop", "editor", "checks")


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
    result = nx(*command, "-t", *TARGETS, "--dry-run", "--graph=stdout", capture=True)
    plan = make_plan(json.loads(result.stdout))
    args.plan.parent.mkdir(parents=True, exist_ok=True)
    args.plan.write_text(json.dumps(plan, indent=2) + "\n", encoding="utf-8")
    selected = {group: bool(tasks) for group, tasks in plan.items()}
    print(json.dumps({"selected": selected, "tasks": plan}, indent=2))
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"selected={json.dumps(selected)}\n")


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
    for target in TARGETS:
        projects = [task["project"] for task in tasks if task["target"] == target]
        if not projects:
            continue
        arguments = [
            "run-many", f"--projects={','.join(projects)}", f"--targets={target}",
            "--parallel=3", "--output-style=static",
        ]
        if target == "e2e-web":
            arguments.append(f"--configuration={args.browser}")
        nx(*arguments, environment={"BROWSER": args.browser})
    if args.group == "unit":
        verify_unit_reports(len(tasks))


def verify_results(needs, expected):
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
    else:
        verify_results(json.loads(args.needs), json.loads(args.expected))
        print("All selected verification jobs succeeded; unselected jobs were skipped.")


if __name__ == "__main__":
    main()
