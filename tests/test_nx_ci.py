"""CI routing, full verification, fail-closed aggregation and real Nx failure tests."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from test_nx_selection import affected_graph

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".github/scripts"))
from nx_ci import GROUPS, TARGETS, make_plan, nx, verify_results


class CiRoutingTests(unittest.TestCase):
    def test_real_inferred_leaf_has_nonempty_compilation_plan(self):
        plan = make_plan(affected_graph("tests/Talewright.BenchmarkDotNet/Program.cs"))
        self.assertIn(
            {"project": "Talewright.BenchmarkDotNet", "target": "build:release"},
            plan["checks"],
        )

    def test_editor_and_documentation_plans_do_not_run_product_jobs(self):
        for changed, expected in [
            ("tooling/vscode-talewright/syntaxes/tale.tmLanguage.json", {"editor"}),
            ("tooling/vscode-talewright/snippets/tale.json", {"editor"}),
            ("docs/guide/first-story.md", {"checks", "editor"}),
        ]:
            with self.subTest(changed=changed):
                plan = make_plan(affected_graph(changed))
                self.assertEqual(expected, {group for group, tasks in plan.items() if tasks})

    def test_fallback_and_full_verification_include_every_environment(self):
        fallback = make_plan(affected_graph("new-product/feature.cs"))
        full = make_plan(json.loads(nx(
            "run-many", "--all", "--exclude=talewright-workspace",
            "-t", *TARGETS, "--dry-run", "--graph=stdout", capture=True,
        ).stdout))
        self.assertEqual(set(GROUPS), {group for group, tasks in full.items() if tasks})
        self.assertEqual(full, fallback)
        self.assertEqual(6, len(full["unit"]))
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
