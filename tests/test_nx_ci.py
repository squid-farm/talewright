"""CI routing, full verification, fail-closed aggregation and real Nx failure tests."""

import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest.mock import patch

from test_nx_selection import NO_IMPACT_FILES, affected_graph

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".github/scripts"))
from nx_ci import GROUPS, TARGETS, make_plan, nx, verify_results, verify_unit_reports


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

    def test_real_inferred_leaf_has_nonempty_compilation_plan(self):
        plan = make_plan(affected_graph("benchmarks/Talewright.BenchmarkDotNet/Program.cs"))
        self.assertIn(
            {"project": "Talewright.BenchmarkDotNet", "target": "build:release"},
            plan["checks"],
        )

    def test_owned_inputs_select_only_consumer_groups(self):
        scenarios = (
            ("samples/LostRelic/chapters/01-camp.tale", {"build", "unit", "editor", "browser", "linux-desktop"}),
            ("samples/LostRelic/story.talewright.yaml", {"build", "unit", "editor", "browser", "linux-desktop"}),
            ("samples/LostRelic/characters/guide.talewright.yaml",
             {"build", "unit", "editor", "browser", "linux-desktop"}),
            ("player/tests/run_e2e.py", {"browser", "linux-desktop"}),
            ("player/tests/native/atspi_adapter.py", {"browser", "linux-desktop"}),
            ("examples/CSharpStory/Directory.Build.props", {"build", "editor"}),
            ("benchmarks/performance/publish_results.py", {"checks"}),
            ("benchmarks/performance/run_publication.py", {"checks"}),
            ("benchmarks/performance/test_publish_results.py", {"checks"}),
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
                        [{"project": "talewright-performance-tooling", "target": "check"}],
                        plan["checks"],
                    )

    def test_mixed_owned_inputs_preserve_all_consumer_tasks(self):
        files = (
            "samples/LostRelic/chapters/01-camp.tale",
            "player/tests/run_e2e.py",
            "examples/CSharpStory/Directory.Build.props",
            "benchmarks/performance/publish_results.py",
        )
        expected = {group: set() for group in GROUPS}
        for changed in files:
            for group, tasks in make_plan(affected_graph(changed)).items():
                expected[group].update((task["project"], task["target"]) for task in tasks)
        actual = make_plan(affected_graph(*files))
        self.assertEqual(expected, {
            group: {(task["project"], task["target"]) for task in tasks}
            for group, tasks in actual.items()
        })

    def test_editor_and_documentation_plans_do_not_run_product_jobs(self):
        for changed, expected in [
            ("tooling/vscode-talewright/syntaxes/tale.tmLanguage.json", {"editor"}),
            ("tooling/vscode-talewright/snippets/tale.json", {"editor"}),
            ("docs/guide/first-story.md", {"checks", "editor"}),
            ("docs/developer/architecture.md", {"checks"}),
            ("docs/reference/diagnostics.md", {"checks"}),
        ]:
            with self.subTest(changed=changed):
                plan = make_plan(affected_graph(changed))
                self.assertEqual(expected, {group for group, tasks in plan.items() if tasks})

    def test_documented_manifests_select_only_schema_verification(self):
        schema_test = ROOT / "tooling/vscode-talewright/tests/story-schema.test.mjs"
        consumed = set(re.findall(r"'(docs/[^']+\.md)'", schema_test.read_text()))
        editor = json.loads((ROOT / "tooling/vscode-talewright/project.json").read_text())
        self.assertEqual(
            {f"{{workspaceRoot}}/{path}" for path in consumed},
            set(editor["namedInputs"]["documentedManifests"]),
        )
        for changed in consumed:
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
        for source in (
            "tooling/vscode-talewright/extension.cjs",
            "engine/src/Talewright.Talemark/Parsing/StoryYamlReader.cs",
            "player/src/Talewright.Player/Components/PlayerToolbar.razor",
        ):
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
        fallback = make_plan(affected_graph("new-product/feature.cs"))
        full = make_plan(json.loads(nx(
            "run-many", "--all", "--exclude=talewright-workspace",
            "-t", *TARGETS, "--dry-run", "--graph=stdout", capture=True,
        ).stdout))
        self.assertEqual(set(GROUPS), {group for group, tasks in full.items() if tasks})
        self.assertEqual(full, fallback)
        self.assertIn(
            {"project": "talewright-performance-tooling", "target": "check"}, full["checks"],
        )
        self.assertEqual(8, len(full["unit"]))
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


class InfrastructureSelectionTests(unittest.TestCase):
    SCENARIOS = {
        ".github/scripts/measure_dependency_reuse.py": {"checks"},
        ".github/scripts/nx_ci.py": {"checks"},
        ".github/scripts/package_version.py": {"build", "editor", "checks"},
        ".github/scripts/github_release.py": {"build", "checks"},
        ".github/scripts/package_cli.sh": {"build", "checks"},
        ".github/scripts/verify_release_vsix.py": {"build", "editor", "checks"},
        ".github/scripts/publish_samples.py": {"browser", "checks"},
        ".github/scripts/ui_test_image_plan.py": {"checks"},
        ".github/scripts/verify_packages.py": {"build", "checks"},
        ".github/workflows/build.yml": {"build", "checks"},
        ".github/workflows/editor-tooling.yml": {"editor", "checks"},
        ".github/workflows/performance.yml": {"checks"},
        ".github/workflows/publish-nuget.yml": {"build", "checks"},
        ".github/workflows/publish-samples.yml": {"browser", "checks"},
        ".github/workflows/publish-ui-test-image.yml": {"checks"},
        ".github/workflows/tests.yml": set(GROUPS) - {"build"},
        ".github/docker/ui-tests/Dockerfile": {"checks"},
        ".github/docker/ui-tests/image.json": {"checks"},
        ".github/docker/ui-tests/verify.py": {"checks"},
    }

    def test_every_current_workflow_script_and_image_file_has_a_scenario(self):
        files = {
            path.relative_to(ROOT).as_posix()
            for folder in ("scripts", "workflows", "docker")
            for path in (ROOT / ".github" / folder).rglob("*")
            if path.is_file() and "__pycache__" not in path.parts
        }
        self.assertEqual(files, set(self.SCENARIOS))

    def test_infrastructure_selects_exact_driven_groups_and_contracts(self):
        full = make_plan(json.loads(nx(
            "run-many", "--all", "--exclude=talewright-workspace",
            "-t", *TARGETS, "--dry-run", "--graph=stdout", capture=True,
        ).stdout))
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

    def test_ci_tests_no_longer_select_product_e2e(self):
        plan = make_plan(affected_graph("tests/test_ci.py"))
        self.assertEqual(
            {group: ([{"project": "talewright-ci-tests", "target": "ci-check"}]
                     if group == "checks" else []) for group in GROUPS},
            plan,
        )

    def test_image_changes_retain_dedicated_candidate_validation(self):
        workflow = (ROOT / ".github/workflows/publish-ui-test-image.yml").read_text()
        triggers = workflow.split("\npermissions:", 1)[0]
        for path in (
            ".github/docker/ui-tests/**", ".github/scripts/ui_test_image_plan.py",
            ".github/workflows/publish-ui-test-image.yml",
        ):
            self.assertEqual(2, triggers.count(f"'{path}'"))
        self.assertIn("docker build -t talewright-ui-tests:candidate", workflow)
        self.assertIn("python3 /opt/talewright-ui/verify.py", workflow)
        self.assertIn("FullyQualifiedName~StoryLoadingTests", workflow)
        self.assertIn("python3 player/tests/run_e2e.py linux --probe", workflow)

    def test_mixed_ci_and_product_changes_retain_both_plans(self):
        files = (".github/scripts/nx_ci.py", "player/src/Talewright.Player/Components/PlayerToolbar.razor")
        expected = {group: set() for group in GROUPS}
        for changed in files:
            for group, tasks in make_plan(affected_graph(changed)).items():
                expected[group].update((task["project"], task["target"]) for task in tasks)
        actual = make_plan(affected_graph(*files))
        self.assertEqual(expected, {
            group: {(task["project"], task["target"]) for task in tasks}
            for group, tasks in actual.items()
        })


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


class WorkflowAggregateTests(unittest.TestCase):
    """Execute the workflows' actual aggregate steps, including their selection guards."""

    def workflow(self, name):
        return (ROOT / ".github/workflows" / name).read_text()

    def aggregate(self, workflow, needs, selected):
        content = self.workflow(workflow)
        verification = content.split("\n  verification:\n", 1)[1].split("\n  publish-", 1)[0]
        self.assertIn("    if: always()\n", verification)
        run = verification.split("        run: |\n", 1)[1]
        return subprocess.run(
            ["bash", "-e", "-c", textwrap.dedent(run)],
            cwd=ROOT, text=True, capture_output=True,
            env=dict(os.environ, NEEDS=json.dumps(needs), SELECTED=json.dumps(selected)),
        )

    def test_no_op_reusable_workflow_and_required_gate_pass(self):
        selected = {group: False for group in GROUPS}
        tests = {"plan": {"result": "success"}, **{
            group: {"result": "skipped"} for group in GROUPS if group != "build"
        }}
        result = self.aggregate("tests.yml", tests, selected)
        self.assertEqual(0, result.returncode, result.stderr)
        result = self.aggregate("build.yml", {
            "select": {"result": "success"}, "build": {"result": "skipped"},
            "tests": {"result": "success"},
        }, selected)
        self.assertEqual(0, result.returncode, result.stderr)

    def test_planning_failure_or_cancellation_cannot_pass(self):
        for workflow, planner in (("build.yml", "select"), ("tests.yml", "plan")):
            for status in ("failure", "cancelled", "skipped"):
                with self.subTest(workflow=workflow, status=status):
                    result = self.aggregate(workflow, {planner: {"result": status}}, {})
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("failed, was skipped, or was cancelled", result.stderr)

    def test_selected_job_failures_and_unexpected_skips_cannot_pass(self):
        selected = {group: True for group in GROUPS}
        for workflow, planner, jobs in (
            ("build.yml", "select", ("build", "tests")),
            ("tests.yml", "plan", tuple(group for group in GROUPS if group != "build")),
        ):
            for job in jobs:
                for status in ("failure", "cancelled", "skipped"):
                    with self.subTest(workflow=workflow, job=job, status=status):
                        needs = {name: {"result": "success"} for name in (planner, *jobs)}
                        needs[job]["result"] = status
                        result = self.aggregate(workflow, needs, selected)
                        self.assertNotEqual(0, result.returncode)
                        self.assertIn(f"{job}: expected success, got {status}", result.stderr)

    def test_workflow_wiring_keeps_no_op_and_failed_runs_reportable(self):
        build = self.workflow("build.yml")
        trigger = build.split("\npermissions:", 1)[0]
        self.assertNotRegex(trigger, r"\bpaths(?:-ignore)?:")
        self.assertIn('if [[ "$EVENT" == pull_request ]]; then', build)
        self.assertIn('select --base "$BASE" --head "$HEAD"', build)
        self.assertIn("select --full", build)
        tests = build.split("\n  tests:\n", 1)[1].split("\n  verification:\n", 1)[0]
        self.assertIn("needs: select", tests)
        self.assertNotIn("if:", tests)
        self.assertIn("uses: ./.github/workflows/tests.yml", tests)
        self.assertIn("selected: ${{ needs.select.outputs.selected }}", tests)
        self.assertIn("name: CI verification", build)
        self.assertIn("needs: [select, build, tests]", build)
        reusable = self.workflow("tests.yml")
        self.assertIn("needs: [plan, unit, browser, linux-desktop, editor, checks]", reusable)
        for group in GROUPS:
            if group == "build":
                continue
            job = reusable.split(f"\n  {group}:\n", 1)[1].split("\n\n", 1)[0]
            self.assertIn("needs: plan", job)
            self.assertIn("if: fromJSON(needs.plan.outputs.selected)", job)


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

    def test_windows_launcher_quotes_wrapper_path(self):
        launcher = (ROOT / "nx.bat").read_text(encoding="utf-8")
        self.assertIn('node "%path_to_root%.nx\\nxw.js" %*', launcher)

    def test_cold_wrapper_install_keeps_graph_stdout_valid_json(self):
        with tempfile.TemporaryDirectory(prefix="talewright-nx-cold-") as directory:
            workspace = Path(directory)
            (workspace / ".nx").mkdir()
            shutil.copyfile(ROOT / ".nx/nxw.js", workspace / ".nx/nxw.js")
            (workspace / "nx.json").write_text(json.dumps({
                "installation": json.loads((ROOT / "nx.json").read_text())["installation"],
                "plugins": [],
            }), encoding="utf-8")
            (workspace / "project.json").write_text(json.dumps({
                "name": "cold-probe",
                "targets": {"check": {"executor": "nx:noop", "cache": False}},
            }), encoding="utf-8")
            environment = dict(os.environ, NX_DAEMON="false", NX_NO_CLOUD="true")
            environment.pop("NX_WRAPPER_SKIP_INSTALL", None)
            result = subprocess.run(
                ["node", workspace / ".nx/nxw.js", "run-many", "-t", "check",
                 "--all", "--dry-run", "--graph=stdout"],
                cwd=workspace, env=environment, capture_output=True, text=True, timeout=180,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            graph = json.loads(result.stdout)
            self.assertIn("cold-probe:check", graph["tasks"]["tasks"])
            self.assertTrue((workspace / ".nx/installation/node_modules/nx").is_dir())

    def test_prerequisites_run_first_and_failure_blocks_dependents(self):
        installation = ROOT / ".nx/installation/node_modules"
        with tempfile.TemporaryDirectory(prefix="talewright-nx-execution-") as directory:
            workspace = Path(directory)
            (workspace / "node_modules").symlink_to(installation, target_is_directory=True)
            (workspace / "nx.json").write_text('{"plugins": []}', encoding="utf-8")
            project = {
                "name": "ordering-probe",
                "targets": {
                    "prepare": {
                        "executor": "nx:run-commands", "cache": False,
                        "options": {"command": "node -e \"require('fs').writeFileSync('prepared', 'yes')\""},
                    },
                    "verify": {
                        "executor": "nx:run-commands", "cache": False,
                        "dependsOn": ["prepare"],
                        "options": {"command": "node -e \"require('fs').readFileSync('prepared'); require('fs').writeFileSync('verified', 'yes')\""},
                    },
                },
            }
            project_file = workspace / "project.json"
            project_file.write_text(json.dumps(project), encoding="utf-8")
            command = [
                "node", str(installation / "nx/dist/bin/nx.js"),
                "run", "ordering-probe:verify", "--output-style=static",
            ]
            environment = dict(os.environ, NX_DAEMON="false", NX_NO_CLOUD="true")
            result = subprocess.run(command, cwd=workspace, env=environment,
                                    capture_output=True, text=True, timeout=60)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertTrue((workspace / "verified").exists())
            (workspace / "prepared").unlink()
            (workspace / "verified").unlink()
            project["targets"]["prepare"]["options"]["command"] = "node -e \"process.exit(17)\""
            project_file.write_text(json.dumps(project), encoding="utf-8")
            result = subprocess.run(command, cwd=workspace, env=environment,
                                    capture_output=True, text=True, timeout=60)
            self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertFalse((workspace / "verified").exists())
            self.assertIn("prepare", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
