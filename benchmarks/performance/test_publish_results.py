import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from publish_results import benchmark_rows, collect, integration_rows, markdown_for_run, update_history


class PublishResultsTests(unittest.TestCase):
    def test_failed_real_corpus_cases_survive_without_measurement_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "firefox.trx").write_text(
                """<TestRun><Results>
<UnitTestResult testName='RealCorpusBrowserPerformanceTests.Load(tierName: "Gigantic")' outcome="Failed">
<Output><ErrorInfo><Message>Restore timed out.</Message></ErrorInfo></Output>
</UnitTestResult>
<UnitTestResult testName="UnrelatedTest" outcome="Failed" />
<UnitTestResult testName="RealCorpusBrowserPerformanceTests.Passed" outcome="Passed" />
</Results></TestRun>""", encoding="utf-8"
            )
            (root / "linux.trx").write_text(
                """<TestRun><Results>
<UnitTestResult testName='RealCorpusNativePerformanceTests.Open(tierName: "Large")' outcome="Failed" />
</Results></TestRun>""", encoding="utf-8"
            )
            rows = integration_rows(root)
            self.assertEqual(2, len(rows))
            self.assertEqual("firefox", rows[0]["browser"])
            self.assertEqual("Gigantic", rows[0]["Metadata"]["Tier"])
            self.assertEqual("Restore timed out.", rows[0]["error"])
            self.assertEqual("Linux Photino", rows[1]["host"])
            run = collect(root)
            report = markdown_for_run(run)
            self.assertIn("Restore timed out.", report)
            self.assertIn("Gigantic", report)
            self.assertIn("Failed", report)

    def test_publication_partial_files_are_used_only_without_directory_summary(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            complete = root / "complete"
            partial = root / "partial"
            complete.mkdir()
            partial.mkdir()
            (complete / "publication-summary.json").write_text(
                json.dumps({"results": [{"name": "web", "status": "succeeded"}]})
            )
            (complete / "publication-web.json").write_text(
                json.dumps({"name": "web", "status": "succeeded"})
            )
            (partial / "publication-linux-x64.json").write_text(
                json.dumps({"name": "linux-x64", "status": "failed"})
            )
            results = collect(root)["publications"]
            self.assertEqual(["web", "linux-x64"], [row["name"] for row in results])
            self.assertEqual("failed", results[1]["status"])

    def test_preserves_actual_benchmark_parameter_columns(self):
        for column, value in (
            ("TierName", "Gigantic"),
            ("CorpusCase", "history-10000"),
            ("Boundary", "zip-total-exact"),
            ("Params", "Large"),
        ):
            with self.subTest(column=column), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                (root / "case-report.csv").write_text(
                    f"Method,{column},Job,Mean,P95,Allocated\n"
                    f"Read,{value},OutOfProcess,1 ms,2 ms,10 KB\n",
                    encoding="utf-8",
                )
                self.assertEqual(f"{column}={value}", benchmark_rows(root)[0]["parameters"])

    def test_command_line_writes_a_summary_and_history_record(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            inputs = root / "input"
            inputs.mkdir()
            output = root / "output"
            history = root / "history"
            script = Path(__file__).with_name("publish_results.py")
            result = subprocess.run(
                [
                    sys.executable,
                    str(script),
                    "--input",
                    str(inputs),
                    "--output",
                    str(output),
                    "--history",
                    str(history),
                ],
                check=True,
                text=True,
                capture_output=True,
                env={
                    **os.environ,
                    "GITHUB_RUN_ID": "43",
                    "GITHUB_RUN_ATTEMPT": "1",
                    "GITHUB_SHA": "def456",
                    "GITHUB_REF_NAME": "main",
                    "GITHUB_EVENT_NAME": "schedule",
                },
            )

            self.assertIn("No structured measurements", result.stdout)
            self.assertTrue((output / "run.json").is_file())
            self.assertTrue((output / "summary.md").is_file())
            self.assertTrue((history / "runs" / "43-1.json").is_file())

    def test_collects_measurements_and_updates_history(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            inputs = root / "input"
            (inputs / "benchmarks").mkdir(parents=True)
            (inputs / "browser").mkdir()
            (inputs / "publication").mkdir()
            (inputs / "benchmarks" / "story-report.csv").write_text(
                "Method,Params,Job,Mean,P95,Allocated\n"
                "LoadAndCompileDirectory,Large,OutOfProcess,100 ms,120 ms,10 MB\n",
                encoding="utf-8",
            )
            (inputs / "benchmarks" / "metadata-Large.json").write_text(
                json.dumps({"corpus": {"tier": "Large"}, "commit": "abc123"}),
                encoding="utf-8",
            )
            (inputs / "browser" / "browser.trx").write_text(
                """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="Failed"><Output><StdOut>
{"host":"browser","browser":"chromium","measurement":"single integration observation; not a percentile","uploadCompileFirstPlayableMs":42,"Metadata":{"Tier":"Large"}}
</StdOut></Output></UnitTestResult></Results></TestRun>""",
                encoding="utf-8",
            )
            (inputs / "publication" / "publication-summary.json").write_text(
                json.dumps({
                    "results": [{
                        "name": "web",
                        "status": "succeeded",
                        "elapsedSeconds": 12.5,
                        "peakWorkingSetKiB": 1234,
                        "artifactBytes": 5678,
                    }]
                }),
                encoding="utf-8",
            )
            old_environment = os.environ.copy()
            try:
                os.environ.update({
                    "GITHUB_RUN_ID": "42",
                    "GITHUB_RUN_ATTEMPT": "2",
                    "GITHUB_SHA": "abc123",
                    "GITHUB_REF_NAME": "m2",
                    "GITHUB_EVENT_NAME": "workflow_dispatch",
                    "PERF_BENCHMARKS_RESULT": "success",
                    "PERF_BROWSER_RESULT": "success",
                    "PERF_PHOTINO_RESULT": "failure",
                })
                run = collect(inputs)
            finally:
                os.environ.clear()
                os.environ.update(old_environment)

            self.assertEqual("42", run["runId"])
            self.assertEqual("success", run["jobResults"]["benchmarksAndPublication"])
            self.assertEqual(1, len(run["benchmarks"]))
            self.assertEqual("120 ms", run["benchmarks"][0]["p95"])
            self.assertEqual(1, len(run["integrations"]))
            self.assertEqual("Failed", run["integrations"][0]["testOutcome"])
            self.assertEqual(1, len(run["publications"]))
            report = markdown_for_run(run)
            self.assertIn("LoadAndCompileDirectory", report)
            self.assertIn("chromium", report)
            self.assertIn("Failed", report)
            self.assertIn("succeeded", report)

            history = root / "history"
            record = update_history(history, run, report)
            self.assertTrue(record.is_file())
            index = (history / "index.md").read_text(encoding="utf-8")
            self.assertIn("abc123", index)
            self.assertIn("[42]", index)
            self.assertTrue((history / "reports" / "42-2.md").is_file())

            unsupported = {
                "schemaVersion": 1,
                "runId": "local-interrupted",
                "runAttempt": 1,
                "createdAt": "2026-01-01T00:00:00Z",
                "completedCoreCases": [{}] * 45,
                "completedBoundaryCases": [{}] * 14,
            }
            (history / "runs" / "local-interrupted.json").write_text(
                json.dumps(unsupported), encoding="utf-8"
            )
            update_history(history, run, report)
            index = (history / "index.md").read_text(encoding="utf-8")
            self.assertNotIn("| 59 |", index)
            self.assertIn("Unsupported archived records", index)
            self.assertNotIn("(reports/local-interrupted.md)", index)
            self.assertIn("(runs/local-interrupted.json)", index)
            self.assertEqual(unsupported, json.loads(
                (history / "runs" / "local-interrupted.json").read_text(encoding="utf-8")))

    def test_history_links_use_actual_record_filenames_and_preserve_attempts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            inputs = root / "input"
            inputs.mkdir()
            history = root / "history"
            run = collect(inputs)
            run.update(runId="42", runAttempt=1, workflowRunUrl="")
            report = markdown_for_run(run)
            record = update_history(history, run, report)
            record.rename(record.with_name("recorded-run.json"))
            (history / "reports" / "42-1.md").rename(history / "reports" / "recorded-run.md")
            run["runAttempt"] = 2
            update_history(history, run, report)
            index = (history / "index.md").read_text(encoding="utf-8")
            self.assertIn("(reports/recorded-run.md)", index)
            self.assertIn("(runs/recorded-run.json)", index)
            self.assertIn("(reports/42-2.md)", index)
            self.assertIn("(runs/42-2.json)", index)

    def test_unsupported_record_fields_are_archived(self):
        cases = [
            ("schemaVersion", 99),
            ("schemaVersion", True),
            ("schemaVersion", 1.0),
            ("benchmarks", None),
            ("integrations", {}),
            ("publications", "unreadable"),
            ("jobResults", None),
            ("jobResults", {}),
        ]
        cases.extend(
            (field, value)
            for field in ("createdAt", "runId", "commit", "workflowRunUrl")
            for value in (None, 42, True, [], {})
        )
        cases.extend(
            ("runAttempt", value)
            for value in (None, "1", True, 1.0, [], {})
        )
        cases.extend(
            ("jobResults", {
                "benchmarksAndPublication": "success",
                "browserPlayer": "success",
                "linuxPhotino": "success",
                field: value,
            })
            for field in ("benchmarksAndPublication", "browserPlayer", "linuxPhotino")
            for value in (None, 42, True, [], {})
        )
        for field, value in cases:
            with self.subTest(field=field, value=value), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                inputs = root / "input"
                inputs.mkdir()
                history = root / "history"
                run = collect(inputs)
                run.update(runId="current", workflowRunUrl="")
                runs = history / "runs"
                runs.mkdir(parents=True)
                unsupported = {**run, field: value}
                path = runs / "unsupported.json"
                original = json.dumps(unsupported)
                path.write_text(original, encoding="utf-8")
                update_history(history, run, markdown_for_run(run))
                index = (history / "index.md").read_text(encoding="utf-8")
                self.assertIn("Unsupported archived records", index)
                self.assertIn("[unsupported](runs/unsupported.json)", index)
                self.assertIn("(runs/current-1.json)", index)
                self.assertNotIn("(reports/unsupported.md)", index)
                self.assertEqual(original, path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
