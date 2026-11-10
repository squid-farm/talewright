#!/usr/bin/env python3
"""Mark external issue submissions for maintainer triage."""

import json
import os
from pathlib import Path
import subprocess

TRUSTED_ASSOCIATIONS = {"OWNER", "MEMBER", "COLLABORATOR"}
TRIAGE_LABEL = "status:triage"


def triage_issue(event: dict, repository: str) -> None:
    if event["action"] != "opened":
        raise ValueError("Issue intake only handles newly opened issues")
    if event["repository"]["full_name"] != repository:
        raise ValueError("Issue event does not match the target repository")

    issue = event["issue"]
    number = issue["number"]
    if issue["author_association"] in TRUSTED_ASSOCIATIONS:
        print(f"Issue #{number}: internal submission; no triage label needed")
        return
    if any(label["name"] == TRIAGE_LABEL for label in issue["labels"]):
        print(f"Issue #{number}: already marked for triage")
        return

    subprocess.run(
        ["gh", "issue", "edit", str(number), "--repo", repository,
         "--add-label", TRIAGE_LABEL],
        check=True,
    )
    print(f"Issue #{number}: marked for maintainer triage")


if __name__ == "__main__":
    triage_issue(
        json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text()),
        os.environ["GITHUB_REPOSITORY"],
    )
