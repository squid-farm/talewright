#!/usr/bin/env python3
"""Build and run opt-in UI suites without a manually managed server."""

import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "tests/Talewright.Player.E2E.Tests"
BIN = PROJECT / "bin/Release/net10.0"


def run(command, **kwargs):
    print("+", " ".join(map(str, command)), flush=True)
    subprocess.run(list(map(str, command)), cwd=ROOT, check=True, **kwargs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("suite", choices=["install-browsers", "web", "linux"])
    parser.add_argument("--browser", choices=["all", "chromium", "firefox", "webkit"], default="all")
    parser.add_argument("--probe", action="store_true", help="Linux accessibility feasibility gate only")
    parser.add_argument("--filter", help="dotnet test filter (web) or unittest name substring (Linux)")
    args = parser.parse_args()
    results = ROOT / "artifacts/test-results"
    results.mkdir(parents=True, exist_ok=True)
    if args.suite in ("web", "install-browsers"):
        run(["dotnet", "build", PROJECT, "-c", "Release"])
        browsers = ["chromium", "firefox", "webkit"] if args.browser == "all" else [args.browser]
        if args.suite == "install-browsers":
            run(["dotnet", "exec", "--runtimeconfig", BIN / "Talewright.Player.E2E.Tests.runtimeconfig.json",
                 BIN / "Microsoft.Playwright.dll", "install", "--with-deps", *browsers])
            return 0
        with tempfile.TemporaryDirectory(prefix="talewright-web-") as temporary:
            run(["dotnet", "publish", "src/Talewright.Host.Web", "-c", "Release", "-o", temporary])
            failed = []
            for browser in browsers:
                environment = dict(os.environ, BROWSER=browser, TALEWRIGHT_WEB_ROOT=str(Path(temporary) / "wwwroot"),
                                   TALEWRIGHT_TEST_RESULTS=str(results))
                command = ["dotnet", "test", PROJECT, "-c", "Release", "--no-build",
                           "--logger", f"trx;LogFileName={browser}.trx", "--results-directory", results]
                if args.filter:
                    command += ["--filter", args.filter]
                report = results / f"{browser}.trx"
                report.unlink(missing_ok=True)
                try:
                    run(command, env=environment)
                except subprocess.CalledProcessError:
                    failed.append(browser)
                    continue
                if not report.is_file():
                    raise RuntimeError(f"Browser {browser} produced no test report")
                counters = ET.parse(report).find(".//{*}Counters")
                if counters is None or int(counters.attrib["total"]) == 0:
                    raise RuntimeError(f"Browser {browser} ran no tests; check --filter")
            if failed:
                print("Failed browsers:", ", ".join(failed), file=sys.stderr)
                return 1
        return 0

    if sys.platform != "linux":
        parser.error("The native suite requires Linux, WebKitGTK, Xvfb, D-Bus, and system Python AT-SPI bindings.")
    if os.geteuid() == 0:
        parser.error("Run the native suite as an unprivileged user; do not disable the WebKit sandbox.")
    run(["dotnet", "build", "src/Talewright.Host.Desktop", "-c", "Release"])
    with tempfile.TemporaryDirectory(prefix="talewright-native-") as temporary:
        temporary = Path(temporary)
        fixtures = temporary / "fixtures"
        run(["dotnet", "run", "--project", "tests/Talewright.TestStories", "-c", "Release", "--", fixtures])
        environment = dict(os.environ, GTK_MODULES="gail:atk-bridge", GIO_USE_VFS="local",
                           GIO_USE_VOLUME_MONITOR="unix")
        for variable, child in [("XDG_CONFIG_HOME", "config"), ("XDG_CACHE_HOME", "cache"),
                                ("XDG_DATA_HOME", "data"), ("XDG_RUNTIME_DIR", "runtime")]:
            directory = temporary / child
            directory.mkdir(mode=0o700)
            environment[variable] = str(directory)
        run(["dpkg-query", "-W", "libwebkit2gtk-4.1-0", "python3-pyatspi", "at-spi2-core", "xfwm4"])
        command = ["xvfb-run", "-a", "-s", "-screen 0 1440x1000x24", "dbus-run-session", "--",
                   "/usr/bin/python3", ROOT / "tests/native/player_tests.py",
                   "--executable", ROOT / "src/Talewright.Host.Desktop/bin/Release/net10.0/Talewright.Host.Desktop.dll",
                   "--fixtures", fixtures, "--output", results / "linux"]
        if args.probe:
            command.append("--probe")
        if args.filter:
            command += ["--filter", args.filter]
        run(command, env=environment)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except subprocess.CalledProcessError as error:
        print(f"Command failed with status {error.returncode}: {error.cmd}", file=sys.stderr)
        sys.exit(error.returncode)
