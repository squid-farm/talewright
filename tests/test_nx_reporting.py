"""CI summaries describe real selection and batch execution without replacing verification."""

from contextlib import redirect_stderr, redirect_stdout
import io
import json
import os
from pathlib import Path
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

    def test_selection_error_is_incomplete_and_still_raises(self):
        for output, stderr in (("Nx graph failed\n", "dependency error\n"), (None, None)):
            with self.subTest(output=output, stderr=stderr):
                error = subprocess.CalledProcessError(
                    7, ["nx", "affected"], output=output, stderr=stderr,
                )
                stdout, diagnostics = io.StringIO(), io.StringIO()
                with patch("nx_ci.nx", side_effect=error), redirect_stdout(stdout), \
                        redirect_stderr(diagnostics), self.assertRaises(
                            subprocess.CalledProcessError
                        ) as caught:
                    nx_ci.select(self.args)
                self.assertIs(error, caught.exception)
                self.assertFalse(self.args.plan.exists())
                self.assertIn("Incomplete", self.summary.read_text())
                self.assertEqual("", stdout.getvalue())
                self.assertEqual((output or "") + (stderr or ""), diagnostics.getvalue())

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

    def test_rerun_clears_stale_success_before_validation(self):
        nx_ci.execution_path(self.args).write_text('{"outcome": "success", "batches": []}')
        self.plan([])
        with self.assertRaisesRegex(ValueError, "unselected"):
            nx_ci.run_group(self.args)
        self.assertFalse(nx_ci.execution_path(self.args).exists())


if __name__ == "__main__":
    unittest.main()
