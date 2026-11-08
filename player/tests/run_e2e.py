#!/usr/bin/env python3
"""Build and run opt-in UI suites without a manually managed server."""

import argparse
from dataclasses import dataclass
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "player/tests/Talewright.Player.E2E.Tests"
BIN = PROJECT / "bin/Release/net10.0"


@dataclass
class Job:
    name: str
    command: list
    environment: dict
    report: Path


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


def parse_shard(value):
    try:
        index, count = map(int, value.split("/"))
    except ValueError:
        raise argparse.ArgumentTypeError("expected INDEX/COUNT, for example 2/3") from None
    if not 1 <= index <= count:
        raise argparse.ArgumentTypeError("shard index must be between 1 and the shard count")
    return index, count


def positive_int(value):
    try:
        number = int(value)
    except ValueError:
        raise argparse.ArgumentTypeError("expected a positive integer") from None
    if number < 1:
        raise argparse.ArgumentTypeError("expected a positive integer")
    return number


def escape_filter_value(value):
    return "".join("\\" + char if char in "\\()&|=!~" else char for char in value)


def name_filter(names):
    return "|".join("FullyQualifiedName=" + escape_filter_value(name) for name in names)


def shard_names(names, shard):
    """Deal sorted test names round-robin so long-running classes spread across shards."""
    index, count = shard
    names = sorted(set(names))
    selected = names[index - 1::count]
    if not selected:
        raise RuntimeError(f"Shard {index}/{count} has no tests")
    print(f"Shard {index}/{count}: {len(selected)} of {len(names)} test methods", flush=True)
    return selected


def shard_filter(names, shard):
    return name_filter(shard_names(names, shard))


def list_tests(selection):
    with tempfile.TemporaryDirectory(prefix="talewright-tests-") as temporary:
        listing = Path(temporary) / "tests.txt"
        run(["dotnet", "vstest", BIN / "Talewright.Player.E2E.Tests.dll", "--ListFullyQualifiedTests",
             f"--TestCaseFilter:{selection}", f"--ListTestsTargetPath:{listing}"], stdout=subprocess.DEVNULL)
        return [line.strip() for line in listing.read_text().splitlines() if line.strip()]


def test_filters(host, selected, shard, jobs):
    """Return one dotnet test filter per concurrent test process."""
    selection = host_filter(host, selected)
    if not shard and jobs == 1:
        return [selection]
    names = sorted(set(list_tests(selection)))
    if shard:
        names = shard_names(names, shard)
    if not names:
        raise RuntimeError("No tests ran; check --filter")
    parts = [names[index::jobs] for index in range(min(jobs, len(names)))]
    if len(parts) > 1:
        print(f"Splitting {len(names)} test methods across {len(parts)} processes", flush=True)
    return [name_filter(part) for part in parts]


def report_name(prefix, index, count):
    return prefix if count == 1 else f"{prefix}-{index}"


def remove_reports(results, prefix):
    (results / f"{prefix}.trx").unlink(missing_ok=True)
    for report in results.glob(f"{prefix}-[0-9]*.trx"):
        report.unlink()


def run_settings(threads):
    return ["--", f"xUnit.MaxParallelThreads={threads}"] if threads else []


def stop(process):
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass


def run_jobs(jobs, limit, results):
    """Run jobs up to limit at a time and return the names of failed jobs."""
    failed = []
    if limit == 1:
        for job in jobs:
            try:
                run(job.command, env=job.environment)
                verify_report(job.report)
            except (subprocess.CalledProcessError, RuntimeError) as error:
                print(f"{job.name} failed: {error}", file=sys.stderr, flush=True)
                failed.append(job.name)
        return failed

    pending = list(jobs)
    running = {}
    try:
        while pending or running:
            while pending and len(running) < limit:
                job = pending.pop(0)
                log_path = results / f"{job.name}.log"
                log = log_path.open("w")
                print(f"+ [{job.name}]", " ".join(map(str, job.command)), flush=True)
                process = subprocess.Popen(list(map(str, job.command)), cwd=ROOT, env=job.environment,
                                           stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                running[process] = (job, log, log_path, time.monotonic())
            time.sleep(0.5)
            for process in [process for process in running if process.poll() is not None]:
                job, log, log_path, started = running.pop(process)
                log.close()
                elapsed = time.monotonic() - started
                error = f"exit status {process.returncode}" if process.returncode else None
                if not error:
                    try:
                        verify_report(job.report)
                    except RuntimeError as problem:
                        error = str(problem)
                if error:
                    failed.append(job.name)
                    tail = log_path.read_text(errors="replace").splitlines()[-40:]
                    print(f"[{job.name}] FAILED after {elapsed:.0f}s ({error}); log: {log_path}", flush=True)
                    print("\n".join(f"[{job.name}] {line}" for line in tail), flush=True)
                else:
                    print(f"[{job.name}] passed in {elapsed:.0f}s; log: {log_path}", flush=True)
    finally:
        for process, (_, log, _, _) in running.items():
            stop(process)
            process.wait()
            log.close()
    return failed


def native_environment(temporary, results):
    environment = dict(os.environ, GTK_MODULES="gail:atk-bridge", GIO_USE_VFS="local",
                       GIO_USE_VOLUME_MONITOR="unix", TALEWRIGHT_TARGET="linux",
                       TALEWRIGHT_TEST_RESULTS=str(results),
                       TALEWRIGHT_DEBUGGER_EXECUTABLE=os.environ.get("TALEWRIGHT_DEBUGGER_EXECUTABLE",
                           str(ROOT / "tooling/debugger/src/Talewright.Debugger/bin/Release/net10.0/Talewright.Debugger.dll")),
                       TALEWRIGHT_CLI_EXECUTABLE=os.environ.get("TALEWRIGHT_CLI_EXECUTABLE",
                           str(ROOT / "tooling/cli/src/Talewright.Cli/bin/Release/net10.0/Talewright.Cli.dll")),
                       TALEWRIGHT_DESKTOP_EXECUTABLE=os.environ.get("TALEWRIGHT_DESKTOP_EXECUTABLE",
                           str(ROOT / "apps/desktop/bin/Release/net10.0/Talewright.Host.Desktop.dll")))
    for variable, child in [("XDG_CONFIG_HOME", "config"), ("XDG_CACHE_HOME", "cache"),
                            ("XDG_DATA_HOME", "data"), ("XDG_RUNTIME_DIR", "runtime")]:
        directory = temporary / child
        directory.mkdir(mode=0o700, parents=True)
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
    return environment


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("suite", choices=["install-browsers", "web", "linux"])
    parser.add_argument("--browser", choices=["all", "chromium", "firefox", "webkit"], default="all")
    parser.add_argument("--probe", action="store_true", help="Linux accessibility feasibility gate only")
    parser.add_argument("--filter", help="dotnet test filter (both hosts)")
    parser.add_argument("--shard", type=parse_shard, default=os.environ.get("TALEWRIGHT_E2E_SHARD") or None,
                        help="run INDEX/COUNT of the selected tests (default: $TALEWRIGHT_E2E_SHARD)")
    parser.add_argument("--jobs", type=positive_int, default=os.environ.get("TALEWRIGHT_E2E_JOBS") or 1,
                        help="concurrent test processes after one build; selected tests are split across them "
                             "(default: $TALEWRIGHT_E2E_JOBS or 1)")
    parser.add_argument("--threads", type=positive_int, default=os.environ.get("TALEWRIGHT_E2E_THREADS") or None,
                        help="test classes run at once inside each browser process "
                             "(default: $TALEWRIGHT_E2E_THREADS or xunit.runner.json)")
    args = parser.parse_args()
    if args.probe and (args.suite != "linux" or args.filter):
        parser.error("--probe is Linux-only and cannot be combined with --filter")
    jobs = 1 if args.probe else args.jobs
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
            selections = test_filters("web", args.filter, args.shard, jobs)
            work = []
            for browser in browsers:
                remove_reports(results, browser)
                environment = dict(os.environ, TALEWRIGHT_TARGET="web", BROWSER=browser,
                                   TALEWRIGHT_WEB_ROOT=str(Path(temporary) / "wwwroot"),
                                   TALEWRIGHT_TEST_RESULTS=str(results))
                for index, selection in enumerate(selections, 1):
                    name = report_name(browser, index, len(selections))
                    work.append(Job(name, ["dotnet", "test", PROJECT, "-c", "Release", "--no-build",
                                           "--logger", f"trx;LogFileName={name}.trx", "--results-directory", results,
                                           "--filter", selection, *run_settings(args.threads)],
                                    environment, results / f"{name}.trx"))
            failed = run_jobs(work, jobs, results)
            if failed:
                print("Failed:", ", ".join(failed), file=sys.stderr)
                return 1
        return 0

    if sys.platform != "linux":
        parser.error("The native suite requires Linux, WebKitGTK, Xvfb, D-Bus, and system Python AT-SPI bindings.")
    if os.geteuid() == 0:
        parser.error("Run the native suite as an unprivileged user; do not disable the WebKit sandbox.")
    run(["dotnet", "build", "apps/desktop", "-c", "Release"])
    run(["dotnet", "build", "tooling/cli/src/Talewright.Cli", "-c", "Release"])
    run(["dotnet", "build", PROJECT, "-c", "Release"])
    run(["dpkg-query", "-W", "libwebkit2gtk-4.1-0", "python3-pyatspi", "at-spi2-core", "xfwm4"])
    selections = ([host_filter("linux", "Category=Probe")] if args.probe
                  else test_filters("linux", args.filter, args.shard, jobs))
    remove_reports(results, "linux")
    with tempfile.TemporaryDirectory(prefix="talewright-native-") as temporary:
        work = []
        for index, selection in enumerate(selections, 1):
            name = report_name("linux", index, len(selections))
            # Each process gets its own X display, accessibility bus, XDG directories and export folders.
            session_output = results / "linux" if len(selections) == 1 else results / "linux" / f"session-{index}"
            work.append(Job(name, ["xvfb-run", "-n", 99 + 10 * (index - 1), "-a", "-s", "-screen 0 1440x1000x24",
                                   "dbus-run-session", "--",
                                   "/usr/bin/python3", ROOT / "player/tests/native/atspi_adapter.py", "session",
                                   "--output", session_output, "--",
                                   "dotnet", "test", PROJECT, "-c", "Release", "--no-build",
                                   "--logger", f"trx;LogFileName={name}.trx", "--results-directory", results,
                                   "--filter", selection,
                                   # Native windows in one process share an X display, accessibility bus and save directory.
                                   "--", "xUnit.ParallelizeTestCollections=false"],
                            native_environment(Path(temporary) / name, results), results / f"{name}.trx"))
        failed = run_jobs(work, jobs, results)
        if failed:
            print("Failed:", ", ".join(failed), file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except subprocess.CalledProcessError as error:
        print(f"Command failed with status {error.returncode}: {error.cmd}", file=sys.stderr)
        sys.exit(error.returncode)
