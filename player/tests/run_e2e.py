#!/usr/bin/env python3
"""Build and run opt-in UI suites without a manually managed server."""

import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "player/tests/Talewright.Player.E2E.Tests"
BIN = PROJECT / "bin/Release/net10.0"


def run(command, **kwargs):
    print("+", " ".join(map(str, command)), flush=True)
    subprocess.run(list(map(str, command)), cwd=ROOT, check=True, **kwargs)


def verify_report(report):
    if not report.is_file():
        raise RuntimeError(f"No test report produced: {report}")
    counters = ET.parse(report).find(".//{*}Counters")
    if counters is None or int(counters.attrib["total"]) == 0:
        raise RuntimeError("No tests ran; check --filter")


def host_filter(host, selected):
    scope = "Host!=Browser" if host == "linux" else "Host!=Linux"
    return f"{scope}&({selected})" if selected else scope


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("suite", choices=["install-browsers", "web", "linux"])
    parser.add_argument("--browser", choices=["all", "chromium", "firefox", "webkit"], default="all")
    parser.add_argument("--probe", action="store_true", help="Linux accessibility feasibility gate only")
    parser.add_argument("--filter", help="dotnet test filter (both hosts)")
    args = parser.parse_args()
    if args.probe and (args.suite != "linux" or args.filter):
        parser.error("--probe is Linux-only and cannot be combined with --filter")
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
            run(["dotnet", "publish", "apps/web", "-c", "Release", "-o", temporary])
            failed = []
            for browser in browsers:
                environment = dict(os.environ, TALEWRIGHT_TARGET="web", BROWSER=browser, TALEWRIGHT_WEB_ROOT=str(Path(temporary) / "wwwroot"),
                                   TALEWRIGHT_TEST_RESULTS=str(results))
                command = ["dotnet", "test", PROJECT, "-c", "Release", "--no-build",
                           "--logger", f"trx;LogFileName={browser}.trx", "--results-directory", results,
                           "--filter", host_filter("web", args.filter)]
                report = results / f"{browser}.trx"
                report.unlink(missing_ok=True)
                try:
                    run(command, env=environment)
                except subprocess.CalledProcessError:
                    failed.append(browser)
                    continue
                verify_report(report)
            if failed:
                print("Failed browsers:", ", ".join(failed), file=sys.stderr)
                return 1
        return 0

    if sys.platform != "linux":
        parser.error("The native suite requires Linux, WebKitGTK, Xvfb, D-Bus, and system Python AT-SPI bindings.")
    if os.geteuid() == 0:
        parser.error("Run the native suite as an unprivileged user; do not disable the WebKit sandbox.")
    run(["dotnet", "build", "apps/desktop", "-c", "Release"])
    run(["dotnet", "build", "tooling/debugger/src/Talewright.Debugger", "-c", "Release"])
    run(["dotnet", "build", PROJECT, "-c", "Release"])
    with tempfile.TemporaryDirectory(prefix="talewright-native-") as temporary:
        temporary = Path(temporary)
        environment = dict(os.environ, GTK_MODULES="gail:atk-bridge", GIO_USE_VFS="local",
                           GIO_USE_VOLUME_MONITOR="unix", TALEWRIGHT_TARGET="linux",
                           TALEWRIGHT_TEST_RESULTS=str(results),
                           TALEWRIGHT_DEBUGGER_EXECUTABLE=os.environ.get("TALEWRIGHT_DEBUGGER_EXECUTABLE",
                               str(ROOT / "tooling/debugger/src/Talewright.Debugger/bin/Release/net10.0/Talewright.Debugger.dll")),
                           TALEWRIGHT_DESKTOP_EXECUTABLE=os.environ.get("TALEWRIGHT_DESKTOP_EXECUTABLE",
                               str(ROOT / "apps/desktop/bin/Release/net10.0/Talewright.Host.Desktop.dll")))
        for variable, child in [("XDG_CONFIG_HOME", "config"), ("XDG_CACHE_HOME", "cache"),
                                ("XDG_DATA_HOME", "data"), ("XDG_RUNTIME_DIR", "runtime")]:
            directory = temporary / child
            directory.mkdir(mode=0o700)
            environment[variable] = str(directory)
        # Pin GTK settings instead of inheriting distro overrides: the upstream file chooser default
        # (startup-mode=recent) ignores a typed location when Open is activated.
        # Test-only deviation: native automation cannot accept Photino's GTK save dialog, so map
        # exports bypass it. See MapExportDialog.TestExportDirectoryVariable.
        exports = temporary / "map-exports"
        exports.mkdir()
        environment["TALEWRIGHT_TEST_ONLY_MAP_EXPORT_DIRECTORY"] = str(exports)
        # Save exports have the same test-only native dialog bypass; see DesktopSaveFileDialog.
        save_exports = temporary / "save-exports"
        save_exports.mkdir()
        environment["TALEWRIGHT_TEST_ONLY_SAVE_EXPORT_DIRECTORY"] = str(save_exports)
        environment["GSETTINGS_BACKEND"] = "keyfile"
        settings = temporary / "config/glib-2.0/settings"
        settings.mkdir(parents=True)
        (settings / "keyfile").write_text("[org/gtk/settings/file-chooser]\nstartup-mode='cwd'\n")
        run(["dpkg-query", "-W", "libwebkit2gtk-4.1-0", "python3-pyatspi", "at-spi2-core", "xfwm4"])
        command = ["xvfb-run", "-a", "-s", "-screen 0 1440x1000x24", "dbus-run-session", "--",
                   "/usr/bin/python3", ROOT / "player/tests/native/atspi_adapter.py", "session",
                   "--output", results / "linux", "--",
                   "dotnet", "test", PROJECT, "-c", "Release", "--no-build",
                   "--logger", "trx;LogFileName=linux.trx", "--results-directory", results,
                   "--filter", host_filter("linux", "Category=Probe" if args.probe else args.filter)]
        report = results / "linux.trx"
        report.unlink(missing_ok=True)
        run(command, env=environment)
        verify_report(report)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except subprocess.CalledProcessError as error:
        print(f"Command failed with status {error.returncode}: {error.cmd}", file=sys.stderr)
        sys.exit(error.returncode)
