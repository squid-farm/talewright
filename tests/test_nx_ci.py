"""CI routing, full verification, fail-closed aggregation and real Nx failure tests."""

import functools
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from test_nx_selection import NO_IMPACT_FILES, affected_graph

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".github/scripts"))
from nx_ci import (
    CHECK_TARGETS, GROUPS, TARGETS, make_plan, nx, verify_results, verify_unit_reports,
    with_complete_checks,
)


@functools.lru_cache(maxsize=None)
def full_graph():
    return json.loads(nx(
        "run-many", "--all", "--exclude=talewright-workspace",
        "-t", *TARGETS, "--dry-run", "--graph=stdout", capture=True,
    ).stdout)


class UnitReportTests(unittest.TestCase):
    def test_reports_are_collected_from_each_area_not_the_old_root_tests(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for area in ("engine/tests/Engine.Tests", "player/tests/Player.Tests",
                         "tooling/authoring/tests/Authoring.Tests", "tooling/cli/tests/Cli.Tests"):
                report = root / area / "artifacts/unit/test.trx"
                report.parent.mkdir(parents=True)
                report.write_text('<TestRun><Counters executed="1" failed="0" /></TestRun>')
            legacy = root / "tests/Old.Tests/artifacts/unit/old.trx"
            legacy.parent.mkdir(parents=True)
            legacy.write_text("not a current report")
            with patch("nx_ci.ROOT", root):
                verify_unit_reports(4)
                with self.assertRaisesRegex(ValueError, "Expected 5 unit reports"):
                    verify_unit_reports(5)


class CiRoutingTests(unittest.TestCase):
    def test_no_impact_plan_has_no_selected_groups(self):
        self.assertEqual(
            {group: [] for group in GROUPS}, make_plan(affected_graph(*NO_IMPACT_FILES)),
        )

    def test_owned_inputs_select_only_consumer_groups(self):
        scenarios = (
            ("samples/LostRelic/chapters/01-camp.tale", {"build", "unit", "editor", "browser", "linux-desktop"}),
            ("player/tests/run_e2e.py", {"browser", "linux-desktop", "checks"}),
            ("examples/CSharpStory/Directory.Build.props", {"build", "editor"}),
            ("benchmarks/performance/publish_results.py", {"checks"}),
        )
        for changed, groups in scenarios:
            with self.subTest(changed=changed):
                graph = affected_graph(changed)
                self.assertFalse(any(
                    task.startswith("talewright-workspace:") for task in graph["tasks"]["tasks"]
                ))
                plan = make_plan(graph)
                self.assertEqual(groups, {group for group, tasks in plan.items() if tasks})
                self.assertEqual(
                    [{"project": "Talewright.Talemark.Tests", "target": "test"}] if "unit" in groups else [],
                    plan["unit"],
                )
                if changed.startswith("samples/"):
                    self.assertIn(
                        {"project": "talewright-editor", "target": "compiler-contract"}, plan["editor"],
                    )
                if "browser" in groups:
                    self.assertEqual(
                        {"e2e-web", "e2e-published-web"},
                        {task["target"] for task in plan["browser"]},
                    )
                    self.assertEqual(
                        {"e2e-linux", "e2e-published-linux"},
                        {task["target"] for task in plan["linux-desktop"]},
                    )
                if "checks" in groups:
                    self.assertEqual(
                        [{"project": ("talewright-e2e-runner" if changed.startswith("player/")
                                      else "talewright-performance-tooling"), "target": "check"}],
                        plan["checks"],
                    )

    def test_editor_and_documentation_plans_select_expected_groups(self):
        for changed, expected in [
            ("tooling/vscode-talewright/syntaxes/tale.tmLanguage.json", {"editor"}),
            ("tooling/vscode-talewright/snippets/tale.json", {"editor"}),
            ("docs/guide/first-story.md", {"checks", "editor"}),
            ("docs/developer/architecture.md", {"checks"}),
            ("docs/reference/diagnostics.md", {"checks", "unit"}),
        ]:
            with self.subTest(changed=changed):
                plan = make_plan(affected_graph(changed))
                self.assertEqual(expected, {group for group, tasks in plan.items() if tasks})

    def test_diagnostic_reference_selects_compiler_units_and_docs_only(self):
        plan = make_plan(affected_graph("docs/reference/diagnostics.md"))
        self.assertEqual(
            [{"project": "Talewright.Talemark.Tests", "target": "test"}], plan["unit"],
        )
        self.assertEqual(
            [{"project": "talewright-docs", "target": "check"}], plan["checks"],
        )
        for group in ("build", "editor", "browser", "linux-desktop"):
            self.assertEqual([], plan[group])

    def test_documented_manifests_select_only_schema_verification(self):
        schema_test = ROOT / "tooling/vscode-talewright/tests/story-schema.test.mjs"
        consumed = set(re.findall(r"'(docs/[^']+\.md)'", schema_test.read_text()))
        editor = json.loads((ROOT / "tooling/vscode-talewright/project.json").read_text())
        self.assertEqual(
            {f"{{workspaceRoot}}/{path}" for path in consumed},
            set(editor["namedInputs"]["documentedManifests"]),
        )
        for changed in sorted(consumed)[:1]:
            with self.subTest(changed=changed):
                graph = affected_graph(changed)
                plan = make_plan(graph)
                self.assertEqual(
                    [{"project": "talewright-docs", "target": "check"}], plan["checks"],
                )
                self.assertEqual({
                    ("talewright-editor", "test"),
                    ("talewright-editor", "compiler-contract"),
                }, {(task["project"], task["target"]) for task in plan["editor"]})
                for group in ("build", "unit", "browser", "linux-desktop"):
                    self.assertEqual([], plan[group])
                self.assertIn(
                    "Talewright.Talemark.Tests:build:release", graph["tasks"]["tasks"],
                )

    def test_general_docs_do_not_hide_mixed_source_verification(self):
        for source in ("player/src/Talewright.Player/Components/PlayerToolbar.razor",):
            with self.subTest(source=source):
                expected = {
                    group: {(task["project"], task["target"]) for task in tasks}
                    for group, tasks in make_plan(affected_graph(source)).items()
                }
                expected["checks"].add(("talewright-docs", "check"))
                actual = make_plan(affected_graph(source, "docs/developer/architecture.md"))
                self.assertEqual(expected, {
                    group: {(task["project"], task["target"]) for task in tasks}
                    for group, tasks in actual.items()
                })

    def test_fallback_and_full_verification_include_every_environment(self):
        fallback = make_plan(affected_graph("src/Talewright.NewModule/Feature.cs"))
        full = make_plan(full_graph())
        self.assertEqual(set(GROUPS), {group for group, tasks in full.items() if tasks})
        self.assertEqual(full, fallback)
        self.assertIn(
            {"project": "talewright-performance-tooling", "target": "check"}, full["checks"],
        )
        self.assertIn(
            {"project": "talewright-e2e-runner", "target": "check"}, full["checks"],
        )
        self.assertEqual(9, len(full["unit"]))
        self.assertIn(
            {"project": "Talewright.Graphs.Tests", "target": "test"}, full["unit"],
        )
        self.assertIn(
            {"project": "Talewright.Authoring.Tests", "target": "test"}, full["unit"],
        )
        self.assertIn(
            {"project": "Talewright.LanguageServer.Tests", "target": "test"}, full["unit"],
        )
        self.assertNotIn(
            {"project": "Talewright.Player.E2E.Tests", "target": "test"}, full["unit"],
        )
        self.assertEqual(
            {"e2e-web", "e2e-published-web"},
            {task["target"] for task in full["browser"]},
        )

    def test_player_javascript_suite_is_selected_for_tests_and_implementation(self):
        for changed in ("player/src/Talewright.Player/Components/MediaView.razor.js",):
            with self.subTest(changed=changed):
                plan = make_plan(affected_graph(changed))
                self.assertIn(
                    {"project": "talewright-e2e-runner", "target": "check"}, plan["checks"],
                )
                self.assertIn(
                    {"project": "Talewright.Player.E2E.Tests", "target": "e2e-web"}, plan["browser"],
                )

    def test_core_grammar_changes_keep_all_regular_and_published_player_suites(self):
        plan = make_plan(affected_graph("engine/src/Talewright.Core/World/Pronouns.cs"))
        for group, targets in (
            ("browser", {"e2e-web", "e2e-published-web"}),
            ("linux-desktop", {"e2e-linux", "e2e-published-linux"}),
        ):
            self.assertEqual(
                targets,
                {task["target"] for task in plan[group]
                 if task["project"] == "Talewright.Player.E2E.Tests"},
            )
        self.assertIn({"project": "Talewright.Core.Tests", "target": "test"}, plan["unit"])
        self.assertIn({"project": "Talewright.Narrative.Tests", "target": "test"}, plan["unit"])

    def test_bad_git_revisions_fail_selection_without_a_success_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "plan.json"
            result = subprocess.run(
                [sys.executable, ROOT / ".github/scripts/nx_ci.py", "select",
                 "--base=not-a-real-revision", "--head=HEAD", f"--plan={manifest}"],
                cwd=ROOT, text=True, capture_output=True,
                env=dict(os.environ, NX_DAEMON="false"),
            )
            self.assertNotEqual(0, result.returncode)
            self.assertFalse(manifest.exists())


class PullRequestChecksTests(unittest.TestCase):
    """Pull requests run every workspace check while other groups stay affected."""

    @classmethod
    def setUpClass(cls):
        cls.checks = json.loads(nx(
            "run-many", "--all", "--exclude=talewright-workspace",
            "-t", *CHECK_TARGETS, "--dry-run", "--graph=stdout", capture=True,
        ).stdout)
        cls.all_checks = make_plan(cls.checks)["checks"]

    def test_pull_requests_select_all_checks_and_affected_other_groups(self):
        for files in (
            NO_IMPACT_FILES,
            ("docs/developer/architecture.md",),
            ("benchmarks/Talewright.BenchmarkDotNet/Program.cs",),
        ):
            with self.subTest(files=files):
                affected = affected_graph(*files)
                plan = make_plan(with_complete_checks(affected, self.checks))
                expected = make_plan(affected)
                for group in set(GROUPS) - {"checks"}:
                    self.assertEqual(expected[group], plan[group])
                checks = [(task["project"], task["target"]) for task in plan["checks"]]
                self.assertEqual(len(checks), len(set(checks)), "Duplicated check tasks")
                self.assertTrue(all(task in plan["checks"] for task in self.all_checks))
                prerequisites = set(self.checks["tasks"]["tasks"])
                for task in expected["checks"]:
                    self.assertTrue(
                        task in plan["checks"]
                        or f"{task['project']}:{task['target']}" in prerequisites,
                        f"Omitted {task}",
                    )
                self.assertFalse(any(
                    task["target"] == "build:release" and task not in expected["checks"]
                    for task in plan["checks"]
                ), "Check prerequisites must stay with Nx, not the plan")

    def test_selection_writes_plan_and_scopes(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "plan.json"
            summary = Path(directory) / "summary.md"
            output = Path(directory) / "output"
            subprocess.run(
                [sys.executable, ROOT / ".github/scripts/nx_ci.py", "select",
                 "--base=HEAD", "--head=HEAD", f"--plan={manifest}"],
                cwd=ROOT, text=True, capture_output=True, check=True,
                env=dict(os.environ, NX_DAEMON="false", GITHUB_STEP_SUMMARY=str(summary),
                         GITHUB_OUTPUT=str(output)),
            )
            plan = json.loads(manifest.read_text())
            self.assertEqual(self.all_checks, plan["checks"])
            self.assertFalse(any(plan[group] for group in set(GROUPS) - {"checks"}))
            scopes = json.loads((Path(directory) / "selection.json").read_text())["scopes"]
            self.assertEqual("complete", scopes["checks"])
            self.assertEqual({"affected"}, {scopes[g] for g in set(GROUPS) - {"checks"}})
            self.assertIn('"checks": true', output.read_text())


class InfrastructureSelectionTests(unittest.TestCase):
    SCENARIOS = {
        ".github/scripts/nx_ci.py": {"checks"},
        ".github/scripts/package_version.py": {"build", "editor", "checks"},
        ".github/scripts/publish_samples.py": {"browser", "checks"},
        ".github/workflows/tests.yml": set(GROUPS) - {"build"},
    }

    def test_infrastructure_selects_exact_driven_groups_and_contracts(self):
        full = make_plan(full_graph())
        for changed, expected in self.SCENARIOS.items():
            with self.subTest(changed=changed):
                graph = affected_graph(changed)
                self.assertFalse(any(
                    task.startswith("talewright-workspace:") for task in graph["tasks"]["tasks"]
                ))
                plan = make_plan(graph)
                self.assertEqual(expected, {group for group, tasks in plan.items() if tasks})
                checks = [{"project": "talewright-ci-tests", "target": "ci-check"}]
                if changed.endswith("/performance.yml"):
                    checks.append({"project": "talewright-performance-tooling", "target": "check"})
                if changed.endswith("/tests.yml"):
                    checks = full["checks"]
                self.assertEqual(checks, plan["checks"])
                for group in expected - {"checks"}:
                    self.assertEqual(full[group], plan[group])

class AggregateTests(unittest.TestCase):
    def test_success_and_legitimate_skips_pass(self):
        verify_results(
            {"selected": {"result": "success"}, "unselected": {"result": "skipped"}},
            {"selected": True, "unselected": False},
        )

    def test_failure_cancellation_and_unexpected_skip_fail(self):
        for status in ("failure", "cancelled", "skipped"):
            with self.subTest(status=status), self.assertRaises(ValueError):
                verify_results({"job": {"result": status}}, {"job": True})

    def test_unselected_jobs_cannot_mask_failures_or_execution(self):
        for status in ("failure", "cancelled", "success"):
            with self.subTest(status=status), self.assertRaises(ValueError):
                verify_results({"job": {"result": status}}, {"job": False})

    def test_missing_or_invalid_results_fail(self):
        for needs, expected in [
            ({}, {"job": True}),
            ({"extra": {"result": "success"}}, {}),
            ({"job": {"result": "success"}}, {"job": "true"}),
        ]:
            with self.subTest(needs=needs), self.assertRaises(ValueError):
                verify_results(needs, expected)


class NxExecutionTests(unittest.TestCase):
    def test_launcher_preserves_absolute_paths_and_arguments_with_spaces(self):
        with tempfile.TemporaryDirectory(prefix="talewright nx launcher ") as directory:
            workspace = Path(directory)
            (workspace / ".nx").mkdir()
            (workspace / ".nx/nxw.js").write_text(
                "console.log(JSON.stringify(process.argv.slice(2)));", encoding="utf-8",
            )
            launcher = workspace / ("nx.bat" if os.name == "nt" else "nx")
            shutil.copy2(ROOT / launcher.name, launcher)
            arguments = ["show", "project with spaces", "--json"]
            command = [str(launcher), *arguments]
            if os.name == "nt":
                command = ["cmd", "/c", *command]
            result = subprocess.run(
                command, cwd=ROOT, capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual(arguments, json.loads(result.stdout))


if __name__ == "__main__":
    unittest.main()
