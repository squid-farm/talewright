#!/usr/bin/env python3
"""Build and run opt-in UI suites without a manually managed server."""

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
from pathlib import PurePosixPath
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "player/tests/Talewright.Player.E2E.Tests"
BIN = PROJECT / "bin/Release/net10.0"
PREPARED_INPUTS = ROOT / "artifacts/browser-e2e-inputs"
PREPARED_ARCHIVE = ROOT / "artifacts/browser-e2e-inputs.tar.gz"
TEST_OUTPUT = "test-output"
WEB_OUTPUT = "web"
CLI_OUTPUT = "cli"
TEMPLATE_OUTPUT = "templates"
MANIFEST_NAME = "manifest.json"
TEST_ASSEMBLY = "Talewright.Player.E2E.Tests.dll"
MANIFEST_IDENTITY_KEYS = ("source_sha", "run_id", "run_attempt")


@dataclass
class Job:
    name: str
    command: list
    environment: dict
    report: Path


def run(command, **kwargs):
    print("+", " ".join(map(str, command)), flush=True)
    return subprocess.run(list(map(str, command)), cwd=ROOT, check=True, **kwargs)


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


def list_tests(selection, test_assembly=None):
    with tempfile.TemporaryDirectory(prefix="talewright-tests-") as temporary:
        listing = Path(temporary) / "tests.txt"
        run(["dotnet", "vstest", test_assembly or BIN / TEST_ASSEMBLY, "--ListFullyQualifiedTests",
             f"--TestCaseFilter:{selection}", f"--ListTestsTargetPath:{listing}"], stdout=subprocess.DEVNULL)
        return [line.strip() for line in listing.read_text().splitlines() if line.strip()]


def test_filters(host, selected, shard, jobs, test_assembly=None, require_tests=False):
    """Return one dotnet test filter per concurrent test process."""
    selection = host_filter(host, selected)
    if not shard and jobs == 1:
        if require_tests and not list_tests(selection, test_assembly):
            raise RuntimeError("Prepared browser selection contains no tests; check --filter")
        return [selection]
    names = sorted(set(list_tests(selection, test_assembly)))
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


def workflow_identity():
    identity = {
        "source_sha": os.environ.get("TALEWRIGHT_E2E_SOURCE_SHA") or os.environ.get("GITHUB_SHA"),
        "run_id": os.environ.get("GITHUB_RUN_ID"),
        "run_attempt": (os.environ.get("TALEWRIGHT_E2E_PREPARATION_ATTEMPT")
                        or os.environ.get("GITHUB_RUN_ATTEMPT")),
    }
    missing = [key for key, value in identity.items() if not value]
    if missing:
        raise RuntimeError(
            "Prepared browser inputs require GitHub run identity; missing "
            + ", ".join(missing)
        )
    return identity


def git_revision():
    result = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, check=True, text=True, capture_output=True,
    )
    return result.stdout.strip()


def required_prepared_files(inputs):
    test_output = inputs / TEST_OUTPUT
    web_root = inputs / WEB_OUTPUT / "wwwroot"
    templates = inputs / TEMPLATE_OUTPUT
    template_archives = sorted(templates.glob("talewright-publish-template-web-*.zip"))
    missing = []
    required = [
        test_output / TEST_ASSEMBLY,
        test_output / "Talewright.Player.E2E.Tests.deps.json",
        test_output / "Talewright.Player.E2E.Tests.runtimeconfig.json",
        test_output / "Microsoft.Playwright.dll",
        test_output / "xunit.runner.json",
        test_output / "testhost.dll",
        web_root / "index.html",
        inputs / CLI_OUTPUT / "Talewright.Cli.dll",
        inputs / CLI_OUTPUT / "Talewright.Cli.deps.json",
        inputs / CLI_OUTPUT / "Talewright.Cli.runtimeconfig.json",
    ]
    if len(template_archives) != 1:
        missing.append(f"{TEMPLATE_OUTPUT}/talewright-publish-template-web-*.zip")
    else:
        required.append(Path(str(template_archives[0]) + ".sha256"))
    missing.extend(path.relative_to(inputs).as_posix() for path in required if not path.is_file())
    if not any(path.name.casefold().endswith("testadapter.dll")
               for path in test_output.glob("*.dll")):
        missing.append(f"{TEST_OUTPUT}/*testadapter.dll")
    if not (web_root / "_framework").is_dir():
        missing.append(f"{WEB_OUTPUT}/wwwroot/_framework/")
    if missing:
        raise RuntimeError("Prepared browser inputs are incomplete; missing " + ", ".join(missing))
    return test_output / TEST_ASSEMBLY, web_root


def prepared_file_hashes(inputs):
    hashes = {}
    for folder in (TEST_OUTPUT, WEB_OUTPUT, CLI_OUTPUT, TEMPLATE_OUTPUT):
        root = inputs / folder
        if not root.is_dir():
            raise RuntimeError(f"Prepared browser inputs are incomplete; missing {folder}/")
        for path in root.rglob("*"):
            if path.is_symlink():
                raise RuntimeError(f"Prepared browser input must not be a symbolic link: {path}")
            if not path.is_file():
                continue
            digest = hashlib.sha256()
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
            hashes[path.relative_to(inputs).as_posix()] = {
                "sha256": digest.hexdigest(),
                "mode": stat.S_IMODE(path.stat().st_mode),
            }
    if not hashes:
        raise RuntimeError("Prepared browser inputs contain no files")
    return hashes


def write_prepared_manifest(inputs, identity):
    required_prepared_files(inputs)
    manifest = {
        "schema_version": 1,
        **identity,
        "files": prepared_file_hashes(inputs),
    }
    (inputs / MANIFEST_NAME).write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def validate_prepared_inputs(inputs=PREPARED_INPUTS):
    expected_identity = workflow_identity()
    actual_revision = git_revision()
    if actual_revision != expected_identity["source_sha"]:
        raise RuntimeError(
            f"Prepared browser checkout mismatch: expected {expected_identity['source_sha']}, "
            f"but checked out HEAD is {actual_revision}"
        )
    manifest_path = inputs / MANIFEST_NAME
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise RuntimeError(f"Prepared browser input manifest is missing: {manifest_path}") from error
    except json.JSONDecodeError as error:
        raise RuntimeError(f"Prepared browser input manifest is invalid: {error}") from error
    if not isinstance(manifest, dict) or manifest.get("schema_version") != 1:
        raise RuntimeError("Prepared browser input manifest has an unsupported schema")
    for key in MANIFEST_IDENTITY_KEYS:
        if manifest.get(key) != expected_identity[key]:
            raise RuntimeError(
                f"Prepared browser inputs do not match this workflow's {key}: "
                f"expected {expected_identity[key]!r}, found {manifest.get(key)!r}"
            )
    expected_files = manifest.get("files")
    if not isinstance(expected_files, dict) or not all(
        isinstance(path, str)
        and isinstance(details, dict)
        and isinstance(details.get("sha256"), str)
        and isinstance(details.get("mode"), int)
        and not isinstance(details.get("mode"), bool)
        and 0 <= details["mode"] <= 0o777
        for path, details in expected_files.items()
    ):
        raise RuntimeError("Prepared browser input manifest has an invalid file inventory")
    actual_files = prepared_file_hashes(inputs)
    if expected_files != actual_files:
        missing = sorted(expected_files.keys() - actual_files.keys())
        extra = sorted(actual_files.keys() - expected_files.keys())
        changed = sorted(
            path for path in expected_files.keys() & actual_files.keys()
            if expected_files[path]["sha256"] != actual_files[path]["sha256"]
        )
        mode_changed = sorted(
            path for path in expected_files.keys() & actual_files.keys()
            if expected_files[path]["mode"] != actual_files[path]["mode"]
        )
        details = []
        if missing:
            details.append("missing: " + ", ".join(missing[:5]))
        if extra:
            details.append("unexpected: " + ", ".join(extra[:5]))
        if changed:
            details.append("checksum mismatch: " + ", ".join(changed[:5]))
        if mode_changed:
            details.append("file mode mismatch: " + ", ".join(mode_changed[:5]))
        raise RuntimeError("Prepared browser inputs failed verification (" + "; ".join(details) + ")")
    return required_prepared_files(inputs)


def create_prepared_archive(inputs, archive=PREPARED_ARCHIVE):
    if archive.exists():
        raise RuntimeError(f"Refusing to overwrite existing prepared browser archive: {archive}")
    with tarfile.open(archive, "w:gz", compresslevel=1) as output:
        output.add(inputs, arcname=inputs.name)


def extract_prepared_archive(archive=PREPARED_ARCHIVE, inputs=PREPARED_INPUTS):
    if not archive.is_file():
        raise RuntimeError(f"Prepared browser input archive is missing: {archive}")
    if inputs.exists():
        raise RuntimeError(f"Refusing to overwrite existing prepared browser inputs: {inputs}")
    inputs.parent.mkdir(parents=True, exist_ok=True)
    try:
        with tarfile.open(archive, "r:*") as source, tempfile.TemporaryDirectory(
            prefix="browser-e2e-extract-", dir=inputs.parent
        ) as temporary:
            members = source.getmembers()
            for member in members:
                relative = PurePosixPath(member.name)
                if (relative.is_absolute() or ".." in relative.parts or not relative.parts
                        or relative.parts[0] != inputs.name):
                    raise RuntimeError(f"Prepared browser archive has an unsafe path: {member.name}")
                if not (member.isdir() or member.isfile()):
                    raise RuntimeError(f"Prepared browser archive has an unsupported entry: {member.name}")
                if member.mode & ~0o777:
                    raise RuntimeError(f"Prepared browser archive has an unsafe mode: {member.name}")
            # Entry paths, types, and modes were restricted above; preserve Playwright's exact file modes.
            filters = {"filter": "fully_trusted"} if hasattr(tarfile, "fully_trusted_filter") else {}
            source.extractall(temporary, members=members, **filters)
            staged = Path(temporary) / inputs.name
            if not staged.is_dir():
                raise RuntimeError("Prepared browser archive does not contain its input directory")
            staged.rename(inputs)
    except tarfile.TarError as error:
        raise RuntimeError(f"Prepared browser input archive is invalid: {error}") from error


def prepare_browser_inputs(output=PREPARED_INPUTS):
    identity = workflow_identity()
    if git_revision() != identity["source_sha"]:
        raise RuntimeError("Prepared browser build checkout does not match the selected source SHA")
    archive = output.with_suffix(".tar.gz")
    if output.exists() or archive.exists():
        raise RuntimeError(f"Refusing to overwrite existing prepared browser inputs: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="browser-e2e-", dir=output.parent) as temporary:
        staged = Path(temporary) / "inputs"
        staged.mkdir()
        run(["dotnet", "build", PROJECT, "-c", "Release"])
        shutil.copytree(BIN, staged / TEST_OUTPUT)
        run(["dotnet", "publish", ROOT / "apps/web", "-c", "Release",
             "-o", staged / WEB_OUTPUT])
        run(["dotnet", "build", ROOT / "tooling/cli/src/Talewright.Cli", "-c", "Release"])
        cli_output = ROOT / "tooling/cli/src/Talewright.Cli/bin/Release/net10.0"
        shutil.copytree(cli_output, staged / CLI_OUTPUT)
        version_result = run(["dotnet", cli_output / "Talewright.Cli.dll", "--version"],
                             capture_output=True, text=True)
        version = version_result.stdout.strip().split("+", 1)[0]
        if not version:
            raise RuntimeError("The prepared CLI did not report its release version.")
        template_output = Path(temporary) / "publish-templates"
        template_environment = dict(
            os.environ, TALEWRIGHT_PUBLISH_TEMPLATE_OUTPUT=str(template_output),
        )
        run(["bash", ROOT / ".github/scripts/package_publish_template.sh", version, "web"],
            env=template_environment)
        staged_templates = staged / TEMPLATE_OUTPUT
        staged_templates.mkdir()
        for path in template_output.glob("talewright-publish-template-web-*.zip*"):
            shutil.copy2(path, staged_templates / path.name)
        write_prepared_manifest(staged, identity)
        staged.rename(output)
    create_prepared_archive(output, archive)
    print(f"Prepared browser inputs at {output}", flush=True)


def web_test_command(selection, name, results, threads, test_assembly, prepared):
    if prepared:
        return [
            "dotnet", "vstest", test_assembly, f"--TestCaseFilter:{selection}",
            f"--logger:trx;LogFileName={name}.trx", f"--ResultsDirectory:{results}",
            *run_settings(threads),
        ]
    return [
        "dotnet", "test", PROJECT, "-c", "Release", "--no-build",
        "--logger", f"trx;LogFileName={name}.trx", "--results-directory", results,
        "--filter", selection, *run_settings(threads),
    ]


def run_web_tests(args, browsers, results, test_assembly, web_root, prepared):
    selections = test_filters(
        "web", args.filter, args.shard, args.jobs, test_assembly,
        require_tests=prepared,
    )
    work = []
    for browser in browsers:
        remove_reports(results, browser)
        environment = dict(os.environ, TALEWRIGHT_TARGET="web", BROWSER=browser,
                           TALEWRIGHT_WEB_ROOT=str(web_root),
                           TALEWRIGHT_TEST_RESULTS=str(results))
        for index, selection in enumerate(selections, 1):
            name = report_name(browser, index, len(selections))
            work.append(Job(
                name,
                web_test_command(
                    selection, name, results, args.threads, test_assembly, prepared,
                ),
                environment,
                results / f"{name}.trx",
            ))
    failed = run_jobs(work, args.jobs, results)
    if failed:
        print("Failed:", ", ".join(failed), file=sys.stderr)
        return 1
    return 0


def run_published_web(browser, results):
    if not PREPARED_INPUTS.exists():
        extract_prepared_archive()
    test_assembly, _ = validate_prepared_inputs()
    selection = "Artifact=Standalone"
    archive = ROOT / "artifacts/standalone-web.zip"
    web_root = ROOT / "artifacts/standalone-web"
    archive.unlink(missing_ok=True)
    if web_root.exists():
        shutil.rmtree(web_root)
    environment = dict(
        os.environ,
        TALEWRIGHT_PUBLISH_TEMPLATE_DIRECTORY=str(PREPARED_INPUTS / TEMPLATE_OUTPUT),
    )
    run(["dotnet", PREPARED_INPUTS / CLI_OUTPUT / "Talewright.Cli.dll", "publish",
         "samples/LostRelic", "-a", "--target", "web", "--output", archive], env=environment)
    web_root.mkdir(parents=True)
    run(["unzip", "-oq", archive, "-d", web_root])
    name = f"standalone-{browser}"
    report = results / f"{name}.trx"
    remove_reports(results, name)
    environment = dict(os.environ, TALEWRIGHT_STANDALONE="1", TALEWRIGHT_STANDALONE_CREATION="1",
                       TALEWRIGHT_PUBLISHED_CHARACTER="Mara", TALEWRIGHT_WEB_ROOT=str(web_root),
                       TALEWRIGHT_WEB_BASE_PATH="/games/published", TALEWRIGHT_TEST_RESULTS=str(results),
                       BROWSER=browser)
    job = Job(name, web_test_command(selection, name, results, None, test_assembly, True),
              environment, report)
    if run_jobs([job], 1, results):
        return 1
    counters = ET.parse(report).find(".//{*}Counters")
    if counters is None or int(counters.attrib["executed"]) == 0 or int(counters.attrib["failed"]) != 0:
        raise RuntimeError(f"Published browser report has no executed tests or contains failures: {report}")
    return 0


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
    parser.add_argument("suite", choices=["prepare-web", "install-browsers", "web", "published-web", "linux"])
    parser.add_argument("--browser", choices=["all", "chromium", "firefox", "webkit"], default="all")
    parser.add_argument("--prepared", action="store_true",
                        default=os.environ.get("TALEWRIGHT_E2E_PREPARED") == "1",
                        help="consume verified prepared browser inputs without build or publish")
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
    if args.prepared and args.suite not in ("web", "published-web"):
        parser.error("--prepared is only available for web suites")
    jobs = 1 if args.probe else args.jobs
    results = ROOT / "artifacts/test-results"
    results.mkdir(parents=True, exist_ok=True)
    if args.suite == "prepare-web":
        prepare_browser_inputs()
        return 0
    if args.suite == "published-web":
        if not args.prepared or args.browser == "all":
            parser.error("published-web requires --prepared and one browser")
        return run_published_web(args.browser, results)
    if args.suite in ("web", "install-browsers"):
        browsers = ["chromium", "firefox", "webkit"] if args.browser == "all" else [args.browser]
        if args.suite == "install-browsers":
            run(["dotnet", "build", PROJECT, "-c", "Release"])
            run(["dotnet", "exec", "--runtimeconfig", BIN / "Talewright.Player.E2E.Tests.runtimeconfig.json",
                 BIN / "Microsoft.Playwright.dll", "install", "--with-deps", *browsers])
            return 0
        if args.prepared:
            if not PREPARED_INPUTS.exists():
                extract_prepared_archive()
            test_assembly, web_root = validate_prepared_inputs()
            return run_web_tests(args, browsers, results, test_assembly, web_root, prepared=True)
        run(["dotnet", "build", PROJECT, "-c", "Release"])
        with tempfile.TemporaryDirectory(prefix="talewright-web-") as temporary:
            run(["dotnet", "publish", "apps/web", "-c", "Release", "-o", temporary])
            return run_web_tests(
                args, browsers, results, BIN / TEST_ASSEMBLY,
                Path(temporary) / "wwwroot", prepared=False,
            )

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
    except RuntimeError as error:
        print(error, file=sys.stderr)
        sys.exit(1)
