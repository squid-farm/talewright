import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from publish_results import collect, markdown_for_run, update_history


class PublishResultsTests(unittest.TestCase):
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
                """<TestRun><Results><UnitTestResult><Output><StdOut>
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
            self.assertEqual(1, len(run["publications"]))
            report = markdown_for_run(run)
            self.assertIn("LoadAndCompileDirectory", report)
            self.assertIn("chromium", report)
            self.assertIn("succeeded", report)

            history = root / "history"
            record = update_history(history, run, report)
            self.assertTrue(record.is_file())
            index = (history / "index.md").read_text(encoding="utf-8")
            self.assertIn("abc123", index)
            self.assertIn("[42]", index)
            self.assertTrue((history / "reports" / "42-2.md").is_file())


if __name__ == "__main__":
    unittest.main()
