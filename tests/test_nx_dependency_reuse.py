"""Dependency experiment statistics must not turn failures into savings."""

import contextlib
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / ".github/scripts"))
from measure_dependency_reuse import measure, summarize


class DependencyReuseTests(unittest.TestCase):
    def test_pairs_use_matching_trials_and_preserve_negative_savings(self):
        records = [
            {"trial": trial, "condition": condition, "success": True, "install_seconds": seconds}
            for trial, condition, seconds in [
                (2, "warm", 12), (1, "cold", 20), (2, "cold", 10), (1, "warm", 8),
            ]
        ]
        result = summarize(records)
        self.assertEqual(2, result["successful_pairs"])
        self.assertEqual(5, result["median_install_savings_seconds"])
        self.assertEqual(-2, result["minimum_install_savings_seconds"])

    def test_failed_and_incomplete_pairs_are_not_successful_measurements(self):
        result = summarize([
            {"trial": 1, "condition": "cold", "success": False, "install_seconds": 1},
            {"trial": 1, "condition": "warm", "success": True, "install_seconds": 0},
            {"trial": 2, "condition": "cold", "success": True, "install_seconds": 10},
        ])
        self.assertEqual(0, result["successful_pairs"])
        self.assertEqual(1, result["failed_records"])
        self.assertIsNone(result["median_install_savings_seconds"])

    def test_conditions_have_fresh_outputs_and_only_share_the_pair_store(self):
        snapshot = io.BytesIO()
        with tarfile.open(fileobj=snapshot, mode="w"):
            pass
        command = [sys.executable, "-c", """
import os
from pathlib import Path
assert not Path("generated-output").exists()
Path("generated-output").touch()
store = Path(os.environ["npm_config_cache"]) / "download"
assert store.exists() == (Path.cwd().name == "warm")
store.touch()
"""]
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            with patch.dict("measure_dependency_reuse.CANDIDATES",
                            {"fixture": ([command], [])}), contextlib.redirect_stdout(io.StringIO()):
                result = measure("fixture", 3, output, snapshot.getvalue())
            self.assertEqual(3, result["successful_pairs"])
            for trial in range(1, 4):
                pair = output / f"fixture-{trial}"
                self.assertFalse((pair / "cold").exists())
                self.assertFalse((pair / "warm").exists())
                self.assertFalse((pair / "store").exists())
                self.assertTrue((pair / "overhead.json").exists())

    def test_failed_command_is_recorded_and_stops_the_experiment(self):
        snapshot = io.BytesIO()
        with tarfile.open(fileobj=snapshot, mode="w"):
            pass
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            with patch.dict("measure_dependency_reuse.CANDIDATES",
                            {"fixture": ([[sys.executable, "-c", "pass"]],
                                         [[sys.executable, "-c", "raise SystemExit(7)"]])}):
                with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(
                    RuntimeError, "cold-1.log"
                ):
                    measure("fixture", 3, output, snapshot.getvalue())
            data = json.loads((output / "fixture.json").read_text())
            self.assertEqual(1, data["summary"]["failed_records"])
            self.assertEqual(7, data["records"][0]["commands"][-1]["returncode"])
            self.assertEqual(1, len(data["records"]))


if __name__ == "__main__":
    unittest.main()
