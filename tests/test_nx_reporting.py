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

    def test_unit_execution_skips_dependencies_but_keeps_selected_projects(self):
        self.args.group = "unit"
        plan = {g: [] for g in nx_ci.GROUPS}
        plan["unit"] = [{"project": "First.Tests", "target": "test"},
                        {"project": "Second.Tests", "target": "test"}]
        self.args.plan.write_text(json.dumps(plan))
        with patch("nx_ci.nx") as run, patch("nx_ci.unit_reports", return_value=[]), patch(
            "nx_ci.verify_unit_reports"
        ) as verify:
            nx_ci.run_group(self.args)
        self.assertIn("--projects=First.Tests,Second.Tests", run.call_args.args)
        self.assertIn("--excludeTaskDependencies", run.call_args.args)
        self.assertIn("--parallel=3", run.call_args.args)
        verify.assert_called_once_with(2)

    def test_rerun_clears_stale_success_before_validation(self):
        nx_ci.execution_path(self.args).write_text('{"outcome": "success", "batches": []}')
        self.plan([])
        with self.assertRaisesRegex(ValueError, "unselected"):
            nx_ci.run_group(self.args)
        self.assertFalse(nx_ci.execution_path(self.args).exists())

    def test_e2e_failure_summary_includes_failed_test_and_assertion(self):
        self.args.group = "browser"
        plan = {group: [] for group in nx_ci.GROUPS}
        plan["browser"] = [
            {"project": "Talewright.Player.E2E.Tests", "target": "e2e-web"}
        ]
        self.args.plan.write_text(json.dumps(plan))
        report = self.root / "artifacts/test-results/chromium.trx"
        report.parent.mkdir(parents=True)
        report.write_text(
            """<TestRun>
              <ResultSummary><Counters total="1" executed="1" passed="0" failed="1" /></ResultSummary>
              <Results><UnitTestResult testName="Example.CharacterCreationTests.Pronouns"
                outcome="Failed"><Output><ErrorInfo><Message>Assert.Equal() Failure: Value mismatch
                Expected: 4
                Actual: 0</Message></ErrorInfo></Output></UnitTestResult></Results>
            </TestRun>"""
        )
        nx_ci.save_execution(self.args, {
            "outcome": "failure",
            "batches": [{"target": "e2e-web", "projects": ["Talewright.Player.E2E.Tests"],
                         "outcome": "failure", "seconds": 2}],
        })

        with patch("nx_ci.ROOT", self.root):
            nx_ci.execution_summary(self.args)
        summary = self.summary.read_text()
        self.assertIn("Example.CharacterCreationTests.Pronouns", summary)
        self.assertIn("Assert.Equal() Failure: Value mismatch", summary)
        self.assertIn("artifacts/test-results/chromium.trx", summary)
        self.assertNotIn("Expected: 4", summary)

    def test_missing_and_invalid_e2e_reports_are_classified_as_infrastructure(self):
        self.args.group = "browser"
        plan = {group: [] for group in nx_ci.GROUPS}
        plan["browser"] = [
            {"project": "Talewright.Player.E2E.Tests", "target": "e2e-web"}
        ]
        self.args.plan.write_text(json.dumps(plan))
        nx_ci.save_execution(self.args, {"outcome": "failure", "batches": []})

        with patch("nx_ci.ROOT", self.root):
            nx_ci.execution_summary(self.args)
        self.assertIn("No TRX report was produced", self.summary.read_text())
        self.assertIn("runner/infrastructure failure", self.summary.read_text())

        self.summary.unlink()
        report = self.root / "artifacts/test-results/chromium.trx"
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text("<TestRun>")
        with patch("nx_ci.ROOT", self.root):
            nx_ci.execution_summary(self.args)
        self.assertIn("Could not read TRX report", self.summary.read_text())
        self.assertIn("report/runner failure", self.summary.read_text())

        report.write_text(
            '<TestRun><ResultSummary><Counters total="0" executed="0" failed="0" />'
            "</ResultSummary></TestRun>"
        )
        self.summary.unlink()
        with patch("nx_ci.ROOT", self.root):
            nx_ci.execution_summary(self.args)
        self.assertIn("contains no executed tests", self.summary.read_text())
        self.assertIn("No usable test results", self.summary.read_text())

        report.write_text(
            '<TestRun><ResultSummary><Counters total="1" executed="1" passed="1" failed="0" />'
            "</ResultSummary></TestRun>"
        )
        self.summary.unlink()
        with patch("nx_ci.ROOT", self.root):
            nx_ci.execution_summary(self.args)
        self.assertIn("No failed test cases were recorded", self.summary.read_text())
        self.assertIn("outside test assertions", self.summary.read_text())

    def test_failure_summary_caps_test_details_and_points_to_full_report(self):
        self.args.group = "browser"
        plan = {group: [] for group in nx_ci.GROUPS}
        plan["browser"] = [
            {"project": "Talewright.Player.E2E.Tests", "target": "e2e-web"}
        ]
        self.args.plan.write_text(json.dumps(plan))
        report = self.root / "artifacts/test-results/chromium.trx"
        report.parent.mkdir(parents=True)
        results = "\n".join(
            f'<UnitTestResult testName="FailedTest{index:02}" outcome="Failed">'
            "<Output><ErrorInfo><Message>Assertion failed</Message></ErrorInfo></Output>"
            "</UnitTestResult>"
            for index in range(nx_ci.MAX_FAILURES_IN_SUMMARY + 1)
        )
        report.write_text(
            f'<TestRun><ResultSummary><Counters executed="{nx_ci.MAX_FAILURES_IN_SUMMARY + 1}" '
            f'failed="{nx_ci.MAX_FAILURES_IN_SUMMARY + 1}" /></ResultSummary>'
            f"<Results>{results}</Results></TestRun>"
        )
        nx_ci.save_execution(self.args, {"outcome": "failure", "batches": []})

        with patch("nx_ci.ROOT", self.root):
            nx_ci.execution_summary(self.args)
        summary = self.summary.read_text()
        self.assertIn("FailedTest00", summary)
        self.assertIn(f"FailedTest{nx_ci.MAX_FAILURES_IN_SUMMARY - 1:02}", summary)
        self.assertNotIn(f"FailedTest{nx_ci.MAX_FAILURES_IN_SUMMARY:02}", summary)
        self.assertIn("1 additional test failure", summary)
        self.assertIn("artifacts/test-results/chromium.trx", summary)

    def test_e2e_router_streams_output_and_prints_concise_failure_without_traceback(self):
        self.args.group = "browser"
        plan = {group: [] for group in nx_ci.GROUPS}
        plan["browser"] = [
            {"project": "Talewright.Player.E2E.Tests", "target": "e2e-web"}
        ]
        self.args.plan.write_text(json.dumps(plan))
        error = subprocess.CalledProcessError(13, ["nx"])
        stderr = io.StringIO()
        results = self.root / "artifacts/test-results"
        results.mkdir(parents=True)
        stale_report = results / "stale.trx"
        stale_report.write_text("<TestRun />")
        trace = results / "trace.zip"
        trace.write_text("trace")
        with patch("nx_ci.nx", side_effect=error) as run, patch(
            "nx_ci.time.monotonic", side_effect=[10, 12]
        ), patch("nx_ci.ROOT", self.root), redirect_stderr(stderr), self.assertRaises(
            subprocess.CalledProcessError
        ):
            nx_ci.run_group(self.args)
        self.assertIn("--output-style=stream", run.call_args.args)
        self.assertIn("Nx batch failed: e2e-web", stderr.getvalue())
        self.assertFalse(stale_report.exists())
        self.assertTrue(trace.exists())

        stderr = io.StringIO()
        with patch("nx_ci.ROOT", self.root), patch(
            "nx_ci.nx", side_effect=error
        ), patch("nx_ci.time.monotonic", side_effect=[10, 12]), patch(
            "sys.argv", ["nx_ci.py", "run", "browser", f"--plan={self.args.plan}"]
        ), redirect_stderr(stderr), self.assertRaises(SystemExit) as caught:
            nx_ci.main()
        self.assertEqual(13, caught.exception.code)
        self.assertNotIn("Traceback", stderr.getvalue())
        self.assertIn("Nx batch failed: e2e-web", stderr.getvalue())


    def test_passing_e2e_evidence_is_pruned_and_failure_evidence_is_kept(self):
        results = self.root / "artifacts/test-results"
        passed, failed, nested = (results / "chromium" / name for name in ("passed", "failed", "theory"))
        outside = self.root / "outside"
        for directory in (passed, failed, nested, outside):
            directory.mkdir(parents=True)
            (directory / "trace.zip").write_text("trace")

        def result(outcome, *paths):
            output = "\n".join(f"Evidence: {path}" for path in paths)
            return (f'<UnitTestResult testName="T" outcome="{outcome}"><Output><StdOut>Browser: x\n'
                    f"{output}</StdOut></Output></UnitTestResult>")

        (results / "chromium.trx").write_text(
            "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>"
            + result("Passed", passed) + result("Failed", failed) + result("Passed", nested)
            + result("Passed", outside) + result("Passed", results) + "</Results></TestRun>"
        )
        (results / "broken.trx").write_text("<TestRun>")
        with patch("nx_ci.ROOT", self.root), redirect_stdout(io.StringIO()):
            nx_ci.prune_passing_evidence()
        self.assertFalse(passed.exists())
        self.assertFalse(nested.exists())
        self.assertTrue((failed / "trace.zip").exists())
        self.assertTrue((outside / "trace.zip").exists())
        self.assertTrue((results / "chromium.trx").exists())

    def test_e2e_shards_split_regression_suites_and_run_published_smokes_once(self):
        self.args.group = "browser"
        plan = {group: [] for group in nx_ci.GROUPS}
        plan["browser"] = [
            {"project": "Talewright.Player.E2E.Tests", "target": "e2e-web"},
            {"project": "Talewright.Player.E2E.Tests", "target": "e2e-published-web"},
        ]
        self.args.plan.write_text(json.dumps(plan))
        for shard, targets in (
            ((1, 4), ["e2e-web", "e2e-published-web"]),
            ((2, 4), ["e2e-web"]),
            ((3, 4), ["e2e-web"]),
            ((4, 4), ["e2e-web"]),
        ):
            with self.subTest(shard=shard):
                self.args.shard = shard
                with patch("nx_ci.nx") as run, patch("nx_ci.ROOT", self.root), \
                        redirect_stdout(io.StringIO()):
                    nx_ci.run_group(self.args)
                self.assertEqual(
                    [f"--targets={target}" for target in targets],
                    [call.args[2] for call in run.call_args_list],
                )
                environments = [call.kwargs["environment"] for call in run.call_args_list]
                self.assertEqual(f"{shard[0]}/4", environments[0]["TALEWRIGHT_E2E_SHARD"])
                self.assertEqual("1", environments[0]["TALEWRIGHT_E2E_PREPARED"])
                if len(environments) > 1:
                    self.assertEqual("1", environments[1]["TALEWRIGHT_E2E_PREPARED"])
                    self.assertIn("--excludeTaskDependencies", run.call_args_list[1].args)
                self.assertTrue(all("TALEWRIGHT_E2E_SHARD" not in environment
                                    for environment in environments[1:]))
                self.assertTrue(nx_ci.execution_path(self.args).name.endswith(
                    f"-chromium-{shard[0]}-of-4.json"))
                self.assertEqual("success", self.record()["outcome"])

        self.summary.write_text("")
        nx_ci.execution_summary(self.args)
        summary = self.summary.read_text()
        self.assertIn("## Nx execution: browser (chromium, shard 4/4)", summary)
        self.assertIn("| `e2e-published-web` | `Talewright.Player.E2E.Tests` | runs in shard 1 |", summary)

    def test_browser_jobs_run_only_their_selected_target_and_reject_empty_work(self):
        self.args.group = "browser"
        plan = {group: [] for group in nx_ci.GROUPS}
        plan["browser"] = [
            {"project": "Talewright.Player.E2E.Tests", "target": "e2e-web"},
            {"project": "Talewright.Player.E2E.Tests", "target": "e2e-published-web"},
        ]
        self.args.plan.write_text(json.dumps(plan))
        for target in ("e2e-web", "e2e-published-web"):
            self.args.browser_target = target
            with self.subTest(target=target), patch("nx_ci.nx") as run, \
                    patch("nx_ci.ROOT", self.root), redirect_stdout(io.StringIO()):
                nx_ci.run_group(self.args)
                self.assertEqual(1, run.call_count)
                self.assertIn(f"--targets={target}", run.call_args.args)
                self.assertEqual("1", run.call_args.kwargs["environment"]["TALEWRIGHT_E2E_PREPARED"])
        plan["browser"].pop()
        self.args.plan.write_text(json.dumps(plan))
        with self.assertRaisesRegex(ValueError, "unselected"):
            nx_ci.run_group(self.args)

    def test_unsharded_e2e_runs_do_not_request_a_shard(self):
        self.args.group = "linux-desktop"
        plan = {group: [] for group in nx_ci.GROUPS}
        plan["linux-desktop"] = [{"project": "Talewright.Player.E2E.Tests", "target": "e2e-linux"}]
        self.args.plan.write_text(json.dumps(plan))
        with patch("nx_ci.nx") as run, patch("nx_ci.ROOT", self.root), redirect_stdout(io.StringIO()):
            nx_ci.run_group(self.args)
        self.assertNotIn("TALEWRIGHT_E2E_SHARD", run.call_args.kwargs["environment"])
        self.assertEqual("execution-linux-desktop.json", nx_ci.execution_path(self.args).name)


class E2EShardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT / "player/tests"))
        import run_e2e
        cls.run_e2e = run_e2e

    def test_shards_are_disjoint_complete_and_keep_theory_rows_together(self):
        names = [f"Tests.Class{n % 4}.Method{n}" for n in range(11)] + ["Tests.Class0.Method0"]
        selected = []
        for index in (1, 2, 3):
            with redirect_stdout(io.StringIO()):
                expression = self.run_e2e.shard_filter(names, (index, 3))
            selected.append([term.removeprefix("FullyQualifiedName=") for term in expression.split("|")])
        flattened = [name for shard in selected for name in shard]
        self.assertEqual(sorted(set(names)), sorted(flattened))
        self.assertEqual(len(flattened), len(set(flattened)))
        self.assertLessEqual(max(map(len, selected)) - min(map(len, selected)), 1)

    def test_filter_values_are_escaped_and_empty_shards_fail(self):
        with redirect_stdout(io.StringIO()):
            self.assertEqual("FullyQualifiedName=A.B\\(x\\=1\\)",
                             self.run_e2e.shard_filter(["A.B(x=1)"], (1, 1)))
        with self.assertRaisesRegex(RuntimeError, "no tests"):
            self.run_e2e.shard_filter(["A.B"], (2, 2))

    def test_jobs_split_the_shard_into_disjoint_complete_processes(self):
        names = [f"Tests.Class{n % 4}.Method{n}" for n in range(12)]
        with patch.object(self.run_e2e, "list_tests", return_value=names), redirect_stdout(io.StringIO()):
            shard = self.run_e2e.shard_filter(names, (2, 3)).split("|")
            parts = self.run_e2e.test_filters("web", None, (2, 3), 3)
            self.assertEqual(["Host!=Linux"], self.run_e2e.test_filters("web", None, None, 1))
            self.assertEqual(2, len(self.run_e2e.test_filters("web", None, None, 2)))
            self.assertEqual(12, len(self.run_e2e.test_filters("web", None, None, 20)))
        terms = [term for part in parts for term in part.split("|")]
        self.assertEqual(3, len(parts))
        self.assertEqual(sorted(shard), sorted(terms))
        self.assertEqual(len(terms), len(set(terms)))

    def test_shard_values_are_validated(self):
        self.assertEqual((2, 3), self.run_e2e.parse_shard("2/3"))
        self.assertEqual((2, 3), nx_ci.parse_shard("2/3"))
        for value in ("0/2", "3/2", "2", "a/b"):
            with self.subTest(value=value):
                for parse in (self.run_e2e.parse_shard, nx_ci.parse_shard):
                    with self.assertRaises(Exception):
                        parse(value)


class PreparedBrowserInputTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT / "player/tests"))
        import run_e2e
        cls.run_e2e = run_e2e

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.identity = {"source_sha": "abc123", "run_id": "42", "run_attempt": "2"}
        self.identity_environment = {
            "TALEWRIGHT_E2E_SOURCE_SHA": self.identity["source_sha"],
            "GITHUB_RUN_ID": self.identity["run_id"],
            "GITHUB_RUN_ATTEMPT": self.identity["run_attempt"],
        }

    def populate_test_output(self, root):
        root.mkdir(parents=True)
        for name in (
            "Talewright.Player.E2E.Tests.dll",
            "Talewright.Player.E2E.Tests.deps.json",
            "Talewright.Player.E2E.Tests.runtimeconfig.json",
            "Microsoft.Playwright.dll",
            "xunit.runner.json",
            "testhost.dll",
            "xunit.runner.visualstudio.testadapter.dll",
        ):
            (root / name).write_text(name)
        node = root / ".playwright/node/linux-x64/node"
        node.parent.mkdir(parents=True)
        node.write_text("playwright node")
        node.chmod(0o775)

    def populate_web_output(self, root):
        web_root = root / "wwwroot"
        (web_root / "_framework").mkdir(parents=True)
        (web_root / "index.html").write_text("<html></html>")
        (web_root / "_framework" / "app.js").write_text("app")

    def populate_inputs(self, root):
        self.populate_test_output(root / self.run_e2e.TEST_OUTPUT)
        self.populate_web_output(root / self.run_e2e.WEB_OUTPUT)
        self.populate_cli_output(root / self.run_e2e.CLI_OUTPUT)
        self.run_e2e.write_prepared_manifest(root, self.identity)

    def populate_cli_output(self, root):
        root.mkdir(parents=True)
        for name in ("Talewright.Cli.dll", "Talewright.Cli.deps.json",
                     "Talewright.Cli.runtimeconfig.json", "publish-assets/standalone/web/App.razor"):
            file = root / name
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text(name)

    def validate(self, root):
        with patch.dict(os.environ, self.identity_environment), patch.object(
            self.run_e2e, "git_revision", return_value=self.identity["source_sha"]
        ):
            return self.run_e2e.validate_prepared_inputs(root)

    def test_prepared_manifest_binds_run_head_and_every_payload_file(self):
        root = self.root / "inputs"
        self.populate_inputs(root)
        assembly, web_root = self.validate(root)
        self.assertEqual(root / "test-output/Talewright.Player.E2E.Tests.dll", assembly)
        self.assertEqual(root / "web/wwwroot", web_root)

        with patch.dict(os.environ, {**self.identity_environment, "GITHUB_RUN_ID": "43"}), patch.object(
            self.run_e2e, "git_revision", return_value=self.identity["source_sha"]
        ), self.assertRaisesRegex(RuntimeError, "run_id"):
            self.run_e2e.validate_prepared_inputs(root)

        (root / "web/wwwroot/index.html").write_text("changed")
        with patch.dict(os.environ, self.identity_environment), patch.object(
            self.run_e2e, "git_revision", return_value=self.identity["source_sha"]
        ), self.assertRaisesRegex(RuntimeError, "checksum mismatch"):
            self.run_e2e.validate_prepared_inputs(root)

        (root / "web/wwwroot/index.html").unlink()
        with patch.dict(os.environ, self.identity_environment), patch.object(
            self.run_e2e, "git_revision", return_value=self.identity["source_sha"]
        ), self.assertRaisesRegex(RuntimeError, "missing: web/wwwroot/index.html"):
            self.run_e2e.validate_prepared_inputs(root)

    def test_prepared_manifest_rejects_modified_cli(self):
        root = self.root / "inputs"
        self.populate_inputs(root)
        (root / "cli/Talewright.Cli.dll").write_text("modified")
        with self.assertRaisesRegex(RuntimeError, "checksum mismatch"):
            self.validate(root)

    def test_failed_job_rerun_validates_the_retained_preparation_attempt(self):
        inputs = self.root / "inputs"
        self.populate_inputs(inputs)
        environment = {
            **self.identity_environment,
            "GITHUB_RUN_ATTEMPT": "3",
            "TALEWRIGHT_E2E_PREPARATION_ATTEMPT": self.identity["run_attempt"],
        }
        with patch.dict(os.environ, environment), patch.object(
            self.run_e2e, "git_revision", return_value=self.identity["source_sha"]
        ):
            self.run_e2e.validate_prepared_inputs(inputs)

        for change, field in (
            ({"TALEWRIGHT_E2E_PREPARATION_ATTEMPT": "3"}, "run_attempt"),
            ({"GITHUB_RUN_ID": "43"}, "run_id"),
            ({"TALEWRIGHT_E2E_SOURCE_SHA": "other-head"}, "source_sha"),
        ):
            with self.subTest(field=field), patch.dict(os.environ, {**environment, **change}), patch.object(
                self.run_e2e, "git_revision",
                return_value=change.get("TALEWRIGHT_E2E_SOURCE_SHA", self.identity["source_sha"]),
            ), self.assertRaisesRegex(RuntimeError, field):
                self.run_e2e.validate_prepared_inputs(inputs)

    def test_prepared_archive_preserves_executable_playwright_driver_mode(self):
        inputs = self.root / "browser-e2e-inputs"
        archive = self.root / "browser-e2e-inputs.tar.gz"
        extracted = self.root / "extracted/browser-e2e-inputs"
        self.populate_inputs(inputs)
        self.run_e2e.create_prepared_archive(inputs, archive)
        self.run_e2e.extract_prepared_archive(archive, extracted)

        node = extracted / "test-output/.playwright/node/linux-x64/node"
        self.assertEqual(0o775, node.stat().st_mode & 0o777)
        assembly, _ = self.validate(extracted)
        self.assertEqual(extracted / "test-output/Talewright.Player.E2E.Tests.dll", assembly)
        node.chmod(0o644)
        with patch.dict(os.environ, self.identity_environment), patch.object(
            self.run_e2e, "git_revision", return_value=self.identity["source_sha"]
        ), self.assertRaisesRegex(RuntimeError, "file mode mismatch"):
            self.run_e2e.validate_prepared_inputs(extracted)

    def test_preparation_fails_when_release_outputs_are_incomplete(self):
        root = self.root / "inputs"
        self.populate_test_output(root / self.run_e2e.TEST_OUTPUT)
        (root / self.run_e2e.WEB_OUTPUT / "wwwroot").mkdir(parents=True)
        with self.assertRaisesRegex(RuntimeError, "incomplete"):
            self.run_e2e.write_prepared_manifest(root, self.identity)

    def test_preparation_builds_tests_and_publishes_once_with_run_identity(self):
        output = self.root / "artifacts/browser-e2e-inputs"
        binary = self.root / "bin/Release/net10.0"
        commands = []

        def fake_run(command, **kwargs):
            commands.append(command)
            if command[:2] == ["dotnet", "build"] and command[2] == self.run_e2e.PROJECT:
                self.populate_test_output(binary)
            elif command[:2] == ["dotnet", "build"]:
                self.populate_cli_output(self.root / "tooling/cli/src/Talewright.Cli/bin/Release/net10.0")
            else:
                self.assertEqual(["dotnet", "publish"], command[:2])
                self.populate_web_output(Path(command[command.index("-o") + 1]))

        with patch.dict(os.environ, self.identity_environment), patch.object(
            self.run_e2e, "ROOT", self.root
        ), patch.object(self.run_e2e, "BIN", binary), patch.object(
            self.run_e2e, "git_revision", return_value=self.identity["source_sha"]
        ), patch.object(self.run_e2e, "run", side_effect=fake_run):
            self.run_e2e.prepare_browser_inputs(output)
            self.validate(output)

        self.assertEqual(3, len(commands))
        self.assertEqual(["dotnet", "build"], commands[0][:2])
        self.assertEqual(["dotnet", "publish"], commands[1][:2])
        self.assertEqual(["dotnet", "build"], commands[2][:2])
        self.assertTrue(output.with_suffix(".tar.gz").is_file())

    def test_published_smoke_uses_prepared_cli_and_tests_and_checks_report(self):
        inputs = self.root / "inputs"
        assembly = inputs / "test-output/Talewright.Player.E2E.Tests.dll"
        results = self.root / "artifacts/test-results"
        results.mkdir(parents=True)
        commands, jobs = [], []

        def capture_jobs(work, limit, destination):
            jobs.extend(work)
            work[0].report.write_text('<TestRun><Counters executed="16" failed="0" /></TestRun>')
            return []

        with patch.object(self.run_e2e, "ROOT", self.root), patch.object(
            self.run_e2e, "PREPARED_INPUTS", inputs
        ), patch.object(self.run_e2e, "extract_prepared_archive"), patch.object(
            self.run_e2e, "validate_prepared_inputs", return_value=(assembly, inputs / "web")
        ), patch.object(self.run_e2e, "run", side_effect=lambda command, **kwargs: commands.append(command)
        ), patch.object(self.run_e2e, "run_jobs", side_effect=capture_jobs):
            self.assertEqual(0, self.run_e2e.run_published_web("firefox", results))
        self.assertEqual(["dotnet", inputs / "cli/Talewright.Cli.dll", "publish"], commands[0][:3])
        self.assertEqual(["dotnet", "vstest", assembly], jobs[0].command[:3])
        self.assertIn("--TestCaseFilter:Artifact=Standalone", jobs[0].command)
        self.assertEqual("/games/published", jobs[0].environment["TALEWRIGHT_WEB_BASE_PATH"])
        self.assertEqual("firefox", jobs[0].environment["BROWSER"])

    def test_published_smoke_rejects_empty_or_failed_reports(self):
        inputs = self.root / "inputs"
        results = self.root / "artifacts/test-results"
        results.mkdir(parents=True)

        def capture_jobs(work, limit, destination):
            work[0].report.write_text(report)
            return []

        for report in ('<TestRun><Counters executed="0" failed="0" /></TestRun>',
                       '<TestRun><Counters executed="1" failed="1" /></TestRun>'):
            with self.subTest(report=report), patch.object(self.run_e2e, "ROOT", self.root), \
                    patch.object(self.run_e2e, "PREPARED_INPUTS", inputs), \
                    patch.object(self.run_e2e, "extract_prepared_archive"), \
                    patch.object(self.run_e2e, "validate_prepared_inputs",
                                 return_value=(inputs / "tests.dll", inputs / "web")), \
                    patch.object(self.run_e2e, "run"), \
                    patch.object(self.run_e2e, "run_jobs", side_effect=capture_jobs), \
                    self.assertRaisesRegex(RuntimeError, "no executed tests or contains failures"):
                self.run_e2e.run_published_web("chromium", results)

    def test_prepared_consumer_runs_the_downloaded_assembly_without_build_or_publish(self):
        captured = {}
        inputs = self.root / "inputs"
        inputs.mkdir()
        test_assembly = inputs / "test-output/Talewright.Player.E2E.Tests.dll"
        web_root = inputs / "web/wwwroot"

        def capture_jobs(jobs, limit, results):
            captured["jobs"] = jobs
            return []

        with patch.object(self.run_e2e, "ROOT", self.root), patch.object(
            self.run_e2e, "PREPARED_INPUTS", inputs
        ), patch.object(
            self.run_e2e, "extract_prepared_archive"
        ) as extract, patch.object(
            self.run_e2e, "validate_prepared_inputs", return_value=(test_assembly, web_root)
        ), patch.object(
            self.run_e2e, "test_filters", return_value=["Host!=Linux"]
        ), patch.object(
            self.run_e2e, "run_jobs", side_effect=capture_jobs
        ), patch.object(
            self.run_e2e, "run", side_effect=AssertionError("prepared mode must not build or publish")
        ), patch("sys.argv", ["run_e2e.py", "web", "--prepared", "--browser", "chromium"]):
            self.assertEqual(0, self.run_e2e.main())

        extract.assert_not_called()
        command = captured["jobs"][0].command
        self.assertEqual(["dotnet", "vstest"], command[:2])
        self.assertIn(test_assembly, command)
        self.assertEqual(str(web_root), captured["jobs"][0].environment["TALEWRIGHT_WEB_ROOT"])
        self.assertTrue(any(str(argument).startswith("--ResultsDirectory:") for argument in command))

    def test_prepared_runner_rejects_an_empty_selection(self):
        with patch.object(self.run_e2e, "list_tests", return_value=[]), self.assertRaisesRegex(
            RuntimeError, "contains no tests"
        ):
            self.run_e2e.test_filters("web", "FullyQualifiedName~Missing", None, 1,
                                      self.root / "tests.dll", require_tests=True)


if __name__ == "__main__":
    unittest.main()
