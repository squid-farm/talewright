"""Regression coverage for issue intake through both the API and issue forms."""

from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / ".github/scripts"))
from issue_intake import triage_issue


class IssueIntakeTests(unittest.TestCase):
    def event(self, association, labels=()):
        return {
            "action": "opened",
            "repository": {"full_name": "squid-farm/talewright"},
            "issue": {
                "number": 123,
                "author_association": association,
                "labels": [{"name": label} for label in labels],
            },
        }

    @patch("issue_intake.subprocess.run")
    def test_internal_api_submissions_need_no_labels(self, run):
        for association in ("OWNER", "MEMBER", "COLLABORATOR"):
            with self.subTest(association=association):
                triage_issue(self.event(association), "squid-farm/talewright")
        run.assert_not_called()

    @patch("issue_intake.subprocess.run")
    def test_external_submissions_are_labeled_regardless_of_template(self, run):
        for association in ("NONE", "CONTRIBUTOR", "FIRST_TIMER",
                            "FIRST_TIME_CONTRIBUTOR", "MANNEQUIN"):
            for labels in ((), ("bug",)):
                with self.subTest(association=association, labels=labels):
                    run.reset_mock()
                    triage_issue(self.event(association, labels), "squid-farm/talewright")
                    run.assert_called_once_with(
                        ["gh", "issue", "edit", "123", "--repo", "squid-farm/talewright",
                         "--add-label", "status:triage"],
                        check=True,
                    )

    @patch("issue_intake.subprocess.run")
    def test_existing_triage_label_is_preserved(self, run):
        triage_issue(self.event("NONE", ("bug", "status:triage")), "squid-farm/talewright")
        run.assert_not_called()

    @patch("issue_intake.subprocess.run")
    def test_internal_labels_are_not_removed(self, run):
        triage_issue(self.event("OWNER", ("status:triage",)), "squid-farm/talewright")
        run.assert_not_called()

    @patch("issue_intake.subprocess.run")
    def test_label_failure_is_not_reported_as_success(self, run):
        run.side_effect = subprocess.CalledProcessError(1, ["gh"])
        with self.assertRaises(subprocess.CalledProcessError):
            triage_issue(self.event("NONE"), "squid-farm/talewright")

    @patch("issue_intake.subprocess.run")
    def test_other_events_and_repositories_are_rejected(self, run):
        event = self.event("NONE")
        event["action"] = "reopened"
        with self.assertRaises(ValueError):
            triage_issue(event, "squid-farm/talewright")
        with self.assertRaises(ValueError):
            triage_issue(self.event("NONE"), "other/repository")
        run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
