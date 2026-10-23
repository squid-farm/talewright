"""Fail early when the checked-out tests do not match the prebuilt environment."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET


def verify_versions(root, metadata):
    project = ET.parse(root / "tests/Talewright.Player.E2E.Tests/Talewright.Player.E2E.Tests.csproj")
    packages = [package.attrib["Version"] for package in project.findall(".//PackageReference")
                if package.attrib["Include"] == "Microsoft.Playwright"]
    if packages != [metadata["playwright"]]:
        raise ValueError(
            f"Playwright package {packages} does not match image {metadata['playwright']}. "
            "Publish a matching UI image and update the workflow references."
        )
    requested = json.loads((root / "global.json").read_text())["sdk"]["version"]
    if requested != metadata["sdk_request"]:
        raise ValueError(
            f"SDK request {requested} differs from image contract {metadata['sdk_request']}. "
            "Validate and publish a new UI image before updating CI."
        )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    metadata = json.loads(Path("/opt/talewright-ui/image.json").read_text())
    if os.geteuid() == 0:
        raise ValueError("UI tests must run as the image's non-root CI user.")
    if os.environ.get("WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS"):
        raise ValueError("Disabling the WebKit sandbox is not supported.")
    verify_versions(args.root, metadata)
    # Resolve through the checkout's global.json, including its roll-forward policy.
    subprocess.run(["dotnet", "--version"], cwd=args.root, check=True)
    node = subprocess.check_output(["node", "--version"], text=True).strip()
    if int(node.lstrip("v").split(".")[0]) != metadata["node_major"]:
        raise ValueError(f"Expected Node {metadata['node_major']}, found {node}")
    subprocess.run(["/usr/bin/python3", "-c", "import gi, pyatspi"], check=True)
    for directory in [args.root, Path.home(), Path("/tmp")]:
        if not os.access(directory, os.W_OK):
            raise ValueError(f"CI user cannot write required directory: {directory}")
    print(f"Verified non-root UI environment: {metadata['reference']}; Node {node}", flush=True)


if __name__ == "__main__":
    main()
