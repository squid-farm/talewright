"""CI summaries describe real selection and batch execution without replacing verification."""

from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".github/scripts"))
import nx_ci


class ReportingTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.args = SimpleNamespace(plan=self.root / "plan.json", group="editor",
                                    browser="chromium", full=False, base="base", head="head")
        self.summary = self.root / "summary.md"
        self.environment = patch.dict(os.environ, {
            "GITHUB_STEP_SUMMARY": str(self.summary), "NX_CI_ARTIFACT_URL": "",
        })
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def plan(self, tasks):
        plan = {group: [] for group in nx_ci.GROUPS}
        plan[self.args.group] = [
            {"project": "talewright-editor", "target": target} for target in tasks
        ]
        self.args.plan.write_text(json.dumps(plan))
        return plan

    def record(self):
        return json.loads(nx_ci.execution_path(self.args).read_text())

    def test_selection_modes_preserve_authoritative_plan_shape(self):
        for full, fallback, tasks, label in (
            (True, False, ["test"], "Full"),
            (False, False, ["test"], "Affected"),
            (False, True, ["test"], "Conservative workspace fallback"),
            (False, False, [], "No impact"),
        ):
            with self.subTest(label=label):
                self.args.full = full
                plan = self.plan(tasks)
                graph = {"tasks": {"tasks": {
                    "workspace": {"target": {"project": "talewright-workspace"}}
                } if fallback else {}}}
                self.summary.unlink(missing_ok=True)
                with patch("nx_ci.nx", return_value=SimpleNamespace(stdout=json.dumps(graph))), \
                        patch("nx_ci.make_plan", return_value=plan), \
                        redirect_stdout(io.StringIO()):
                    nx_ci.select(self.args)
                self.assertEqual(plan, json.loads(self.args.plan.read_text()))
                text = self.summary.read_text()
                self.assertIn(label, text)
                if not full:
                    self.assertIn("`base` to `head`", text)
                else:
                    self.assertNotIn("Comparison:", text)
                self.assertIn("`talewright-editor:test`" if tasks else "Not selected", text)

    def test_selection_error_is_incomplete_and_still_raises(self):
        error = subprocess.CalledProcessError(7, ["nx", "affected"])
        with patch("nx_ci.nx", side_effect=error), self.assertRaises(
            subprocess.CalledProcessError
        ) as caught:
            nx_ci.select(self.args)
        self.assertIs(error, caught.exception)
        self.assertFalse(self.args.plan.exists())
        self.assertIn("Incomplete", self.summary.read_text())

    def test_local_selection_stdout_remains_machine_readable(self):
        plan = self.plan(["test"])
        output = io.StringIO()
        with patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": ""}), \
                patch("nx_ci.nx", return_value=SimpleNamespace(
                    stdout=json.dumps({"tasks": {"tasks": {}}})
                )), patch("nx_ci.make_plan", return_value=plan), redirect_stdout(output):
            nx_ci.select(self.args)
        self.assertEqual(plan, json.loads(output.getvalue())["tasks"])

    def test_local_explicit_summary_prints_to_stdout(self):
        self.plan(["test"])
        output = io.StringIO()
        with patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": ""}), redirect_stdout(output):
            nx_ci.execution_summary(self.args)
        self.assertIn("Incomplete", output.getvalue())

    def test_batches_keep_target_order_prerequisites_and_actual_elapsed_time(self):
        self.plan(["package-check", "test"])
        with patch("nx_ci.nx") as run, patch(
            "nx_ci.time.monotonic", side_effect=[10, 12.5, 20, 27]
        ):
            nx_ci.run_group(self.args)
        record = self.record()
        self.assertEqual("success", record["outcome"])
        self.assertEqual(["test", "package-check"], [b["target"] for b in record["batches"]])
        self.assertEqual([2.5, 7], [b["seconds"] for b in record["batches"]])
        self.assertEqual(2, run.call_count)
        for call in run.call_args_list:
            self.assertIn("--parallel=3", call.args)
            self.assertNotIn("--excludeTaskDependencies", call.args)
        nx_ci.execution_summary(self.args)
        text = self.summary.read_text()
        self.assertIn("2.50", text)
        self.assertIn("7.00", text)
        self.assertIn("not per-project", text)
        self.assertNotIn("9.50", text)

    def test_failure_preserves_exception_and_leaves_remaining_batches_incomplete(self):
        self.plan(["test", "package-check"])
        error = subprocess.CalledProcessError(13, ["nx"])
        with patch("nx_ci.nx", side_effect=error) as run, patch(
            "nx_ci.time.monotonic", side_effect=[10, 14]
        ), self.assertRaises(subprocess.CalledProcessError) as caught:
            nx_ci.run_group(self.args)
        self.assertIs(error, caught.exception)
        self.assertEqual(1, run.call_count)
        self.assertEqual("failure", self.record()["outcome"])
        self.assertEqual(["failure", "pending"], [b["outcome"] for b in self.record()["batches"]])
        nx_ci.execution_summary(self.args)
        self.assertIn("incomplete (pending) | Not available", self.summary.read_text())

    def test_interruption_never_reports_success(self):
        self.plan(["test"])
        with patch("nx_ci.nx", side_effect=KeyboardInterrupt), patch(
            "nx_ci.time.monotonic", side_effect=[10, 11]
        ), self.assertRaises(KeyboardInterrupt):
            nx_ci.run_group(self.args)
        self.assertEqual("incomplete", self.record()["outcome"])
        nx_ci.execution_summary(self.args)
        self.assertIn("incomplete (running)", self.summary.read_text())

    def test_report_validation_failure_is_not_a_successful_group(self):
        self.args.group = "unit"
        plan = {g: [] for g in nx_ci.GROUPS}
        plan["unit"] = [{"project": "Unit.Tests", "target": "test"}]
        self.args.plan.write_text(json.dumps(plan))
        with patch("nx_ci.nx"), patch("nx_ci.unit_reports", return_value=[]), patch(
            "nx_ci.verify_unit_reports", side_effect=ValueError("missing report")
        ), self.assertRaisesRegex(ValueError, "missing report"):
            nx_ci.run_group(self.args)
        self.assertEqual("failure", self.record()["outcome"])
        self.assertEqual("success", self.record()["batches"][0]["outcome"])

    def test_missing_records_and_plan_do_not_invent_zero_timings(self):
        for plan_exists in (False, True):
            with self.subTest(plan_exists=plan_exists):
                self.summary.unlink(missing_ok=True)
                if plan_exists:
                    self.plan(["test"])
                nx_ci.execution_summary(self.args)
                text = self.summary.read_text()
                self.assertIn("Incomplete", text)
                self.assertNotIn("0.00", text)
                self.assertNotIn("success", text)

    def test_browser_records_are_isolated_and_configuration_is_preserved(self):
        self.args.group = "browser"
        self.args.plan.write_text(json.dumps({
            "browser": [{"project": "E2E", "target": "e2e-web"}],
        }))
        for browser in ("chromium", "firefox", "webkit"):
            self.args.browser = browser
            with patch("nx_ci.nx") as run:
                nx_ci.run_group(self.args)
            self.assertIn(f"--configuration={browser}", run.call_args.args)
            self.assertTrue(nx_ci.execution_path(self.args).exists())
        self.assertEqual(3, len(list(self.root.glob("execution-browser-*.json"))))

    def test_artifact_links_require_actual_upload_output(self):
        self.plan(["test"])
        nx_ci.execution_summary(self.args)
        self.assertNotIn("Uploaded artifact", self.summary.read_text())
        self.summary.unlink()
        with patch.dict(os.environ, {
            "NX_CI_ARTIFACT_URL": "https://github.com/org/repo/actions/runs/1/artifacts/2",
        }):
            nx_ci.execution_summary(self.args)
        self.assertIn("[Uploaded artifact / reports](https://github.com/", self.summary.read_text())
        with patch.dict(os.environ, {"NX_CI_ARTIFACT_URL": "javascript:alert(1)"}), \
                self.assertRaisesRegex(ValueError, "artifact URL"):
            nx_ci.execution_summary(self.args)

    def test_aggregate_reports_missing_cancelled_and_unexpected_skips_then_fails(self):
        for result in ("cancelled", "skipped", None):
            self.summary.unlink(missing_ok=True)
            needs = {"job": {"result": result}} if result else {}
            with self.assertRaises(ValueError):
                nx_ci.verify_results(needs, {"job": True})
            self.assertIn("incomplete", self.summary.read_text())

    def test_summary_escapes_project_and_revision_markup(self):
        self.args.base = "base|\n<script>`"
        plan = self.plan(["test"])
        text = nx_ci.selection_summary(plan, {"tasks": {"tasks": {}}}, self.args)
        self.assertNotIn("<script>", text)
        self.assertIn("&#124;", text)
        self.assertIn("&#96;", text)

    def test_rerun_clears_stale_success_before_validation(self):
        nx_ci.execution_path(self.args).write_text('{"outcome": "success", "batches": []}')
        self.plan([])
        with self.assertRaisesRegex(ValueError, "unselected"):
            nx_ci.run_group(self.args)
        self.assertFalse(nx_ci.execution_path(self.args).exists())

    def test_workflows_report_every_environment_after_artifact_upload(self):
        for workflow, group, artifact_step in (
            ("build.yml", "build", "packages"),
            ("tests.yml", "unit", "results"),
            ("tests.yml", "browser", "results"),
            ("tests.yml", "linux-desktop", "results"),
            ("tests.yml", "checks", None),
            ("editor-tooling.yml", "editor", "extension"),
        ):
            text = (ROOT / ".github/workflows" / workflow).read_text()
            job = "syntax" if group == "editor" else group
            block = re.split(r"\n  [\w-]+:\n", text.split(f"\n  {job}:\n", 1)[1])[0]
            self.assertIn(f"nx_ci.py summary {group}", block)
            summary_step = block.split("- name: Summarize Nx execution", 1)[1]
            self.assertIn("if: always()", summary_step)
            if artifact_step:
                self.assertIn(f"steps.{artifact_step}.outcome == 'success'", summary_step)
                self.assertIn(f"steps.{artifact_step}.outputs.artifact-url", summary_step)
                self.assertLess(block.index("actions/upload-artifact"), block.index("summary " + group))
            if group == "browser":
                self.assertIn("summary browser --browser ${{ matrix.browser }}", block)


if __name__ == "__main__":
    unittest.main()
