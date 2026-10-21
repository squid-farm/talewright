#!/usr/bin/python3
"""Exercise the real Photino window through its Linux accessibility interfaces."""

import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import unittest
import xml.etree.ElementTree as ET

import gi

gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib
import pyatspi


def normalized(text):
    return " ".join(text.split())


def nodes(root):
    stack = [(root, 0)]
    while stack:
        node, depth = stack.pop()
        if depth > 40 or node.getState().contains(pyatspi.STATE_DEFUNCT):
            continue
        yield node
        # WebKit may retire a node between the state check and the child-count query.
        count = node.childCount
        if count < 0:
            continue
        stack.extend((child, depth + 1) for child in
                     (node.getChildAtIndex(index) for index in reversed(range(count))) if child is not None)


def text(root):
    parts = []
    for node in nodes(root):
        if node.name:
            parts.append(node.name)
        try:
            interface = node.queryText()
            parts.append(interface.getText(0, -1))
        except NotImplementedError:
            pass
    return normalized(" ".join(parts))


class Desktop:
    def __init__(self, executable, output, paths=()):
        self.output = output
        output.mkdir(parents=True, exist_ok=True)
        self.log = (output / "app.log").open("w")
        self.process = subprocess.Popen(
            ["dotnet", str(executable), *map(str, paths)],
            cwd=executable.parent,
            stdout=self.log,
            stderr=subprocess.STDOUT,
        )

    def root(self):
        return pyatspi.Registry.getDesktop(0)

    def wait(self, predicate, description, timeout=15):
        deadline = time.monotonic() + timeout
        last_transient = None
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise AssertionError(f"Photino exited with {self.process.returncode}: {self.output / 'app.log'}")
            try:
                result = predicate()
                if result:
                    return result
            except GLib.Error as error:
                last_transient = error
            time.sleep(0.1)
        raise AssertionError(f"Timed out: {description}; last accessibility error: {last_transient}")

    def find(self, name, role=None):
        return next((node for node in nodes(self.root())
                     if normalized(node.name) == name and (role is None or node.getRoleName() == role)), None)

    def named(self, name, role=None):
        return self.wait(lambda: self.find(name, role), f"accessible {role or 'node'} {name!r}")

    def click(self, name):
        node = self.named(name, "push button")
        actions = node.queryAction()
        if actions.nActions == 0 or not actions.doAction(0):
            raise AssertionError(f"Cannot activate button {name!r}")

    def latest(self):
        return self.named("Current passage")

    def open_bundled(self):
        button = self.wait(
            lambda: next((node for node in nodes(self.root())
                          if node.getRoleName() == "push button"
                          and normalized(node.name).endswith(" Built-in")), None),
            "bundled library entry",
        )
        self.click(normalized(button.name))

    def choose(self, label):
        def find_choice():
            choices = self.find("Story choices")
            if choices is None:
                return None
            return next((node for node in nodes(choices)
                         if node.getRoleName() == "push button"
                         and re.sub(r"^\d+\.\s*", "", normalized(node.name)) == label), None)

        node = self.wait(find_choice, f"choice {label!r}")
        if not node.queryAction().doAction(0):
            raise AssertionError(f"Could not activate {label!r}")

    def key(self, name, event=pyatspi.KEY_PRESSRELEASE):
        found, entries = Gdk.Keymap.get_for_display(Gdk.Display.get_default()).get_entries_for_keyval(
            Gdk.keyval_from_name(name))
        if not found:
            raise AssertionError(f"No hardware keycode for {name}")
        pyatspi.Registry.generateKeyboardEvent(entries[0].keycode, None, event)

    def observe(self, step):
        self.wait(lambda: step["Contains"] in text(self.latest()), f"passage contains {step['Contains']!r}")
        if step["Choices"] is not None:
            def choices_match():
                region = self.find("Story choices")
                actual = [] if region is None else [
                    re.sub(r"^\d+\.\s*", "", normalized(node.name))
                    for node in nodes(region) if node.getRoleName() == "push button"
                ]
                return actual == step["Choices"]
            self.wait(choices_match, f"choices equal {step['Choices']!r}")
        for expected in step["Sheet"] or []:
            self.wait(lambda: expected in text(self.named("Character sheet")),
                      f"character sheet contains {expected!r}")
        if step["Ended"]:
            self.named("Play again", "push button")
            self.wait(lambda: "the end" in text(self.root()).casefold(), "terminal story marker")

    def upload(self, path):
        self.named("Story archive", "push button").queryAction().doAction(0)
        self.wait(lambda: any(node.getRoleName() == "file chooser" for node in nodes(self.root())),
                  "native file chooser")
        self.key("Control_L", pyatspi.KEY_PRESS)
        self.key("l")
        self.key("Control_L", pyatspi.KEY_RELEASE)

        def location():
            return next((node for node in nodes(self.root())
                         if node.getRoleName() == "text"
                         and node.getState().contains(pyatspi.STATE_EDITABLE)
                         and node.getState().contains(pyatspi.STATE_FOCUSED)), None)
        self.wait(location, "editable file chooser location").queryEditableText().setTextContents(str(path))
        self.click("Open")

    def evidence(self):
        try:
            with (self.output / "accessibility.txt").open("w") as stream:
                for node in nodes(self.root()):
                    stream.write(f"{node.getRoleName()}: {node.name!r}\n")
                    try:
                        stream.write(f"  text: {node.queryText().getText(0, -1)[:500]!r}\n")
                    except NotImplementedError:
                        pass
            window = Gdk.get_default_root_window()
            screenshot = Gdk.pixbuf_get_from_window(window, 0, 0, window.get_width(), window.get_height())
            if screenshot is None:
                raise RuntimeError("Could not capture the native display")
            screenshot.savev(str(self.output / "screen.png"), "png", [], [])
        except GLib.Error as error:
            print(f"Accessibility evidence failed: {error}", file=sys.stderr)
            raise

    def close(self):
        try:
            self.evidence()
        finally:
            if self.process.poll() is None:
                self.process.terminate()
                try:
                    self.process.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=8)
            self.log.close()


class PlayerTests(unittest.TestCase):
    executable = None
    fixtures = None
    output = None

    def launch(self, *paths):
        app = Desktop(self.executable, self.output / self.id().split(".")[-1], paths)
        self.addCleanup(app.close)
        return app

    def test_accessibility_probe(self):
        app = self.launch()
        app.open_bundled()
        app.latest()
        app.named("Character sheet")
        app.click("Library")
        app.upload(self.fixtures / "linear.zip")
        app.choose("Walk onward")
        app.wait(lambda: app.latest().getState().contains(pyatspi.STATE_FOCUSED), "latest passage focus")
        app.click("Library")
        app.upload(self.fixtures / "custom.zip")
        app.wait(lambda: "Alex's quiet story." in text(app.latest()), "uploaded fixture through native chooser")
        app.choose("Make a friend")
        app.named("Play again", "push button")

    def test_folder_and_multiple_cli_paths(self):
        app = self.launch(self.fixtures / "linear", self.fixtures / "custom.zip")
        app.wait(lambda: "First passage" in text(app.latest()), "first CLI folder auto-start")
        app.click("Library")
        app.named(f"Player Test {self.fixtures / 'linear'}", "push button")
        app.click(f"Custom Player {self.fixtures / 'custom.zip'}")
        app.wait(lambda: "Alex's quiet story." in text(app.latest()), "second CLI ZIP")

    def test_missing_path_error_and_recovery(self):
        missing = self.fixtures / "does not exist.zip"
        app = self.launch(missing)
        app.wait(lambda: "Couldn't open" in text(app.root()), "missing-path error")
        app.open_bundled()
        app.latest()

    def test_restart_resume_and_replacement(self):
        app = self.launch(self.fixtures / "gates.zip")
        app.choose("Collect supplies")
        app.choose("Continue")
        app.click("Library")
        app.click("Back to Player Test Continue where you left off")
        app.wait(lambda: "Inventory: ready." in text(app.latest()), "resume preserves state")
        app.click("Restart")
        app.wait(lambda: "Inventory: empty." in text(app.latest()), "restart clears state")
        app.click("Library")
        app.upload(self.fixtures / "custom-wrapped.zip")
        app.wait(lambda: "Alex's quiet story." in text(app.latest()), "wrapped ZIP replacement")
        app.choose("Make a friend")
        app.click("Play again")
        app.wait(lambda: "Alex's quiet story." in text(app.latest()), "replay")

    def test_unicode_cli_path(self):
        app = self.launch(self.fixtures / "histoire café.zip")
        app.wait(lambda: "First passage" in text(app.latest()), "Unicode and spaces in command-line path")

    def test_exact_upload_boundary(self):
        app = self.launch()
        app.upload(self.fixtures / "at-upload-limit.zip")
        app.wait(lambda: "First passage" in text(app.latest()), "exact 20 MiB upload", timeout=45)

    def test_seeded_replay(self):
        app = self.launch(self.fixtures / "seeded.zip")
        app.choose("Roll once")
        app.named("Play again", "push button")
        first = text(app.latest())
        self.assertIn("Athletics check:", first)
        app.click("Restart")
        app.choose("Roll once")
        app.named("Play again", "push button")
        self.assertEqual(first, text(app.latest()))

    def test_native_resize_and_new_passage_focus(self):
        app = self.launch(self.fixtures / "long.zip")
        app.latest()
        window = app.named("Talewright", "frame")
        subprocess.run(["wmctrl", "-r", "Talewright", "-e", "0,-1,-1,700,900"], check=True)
        app.wait(lambda: window.queryComponent().getSize()[0] < 750, "native window reaches narrow width")
        def sheet_stacked():
            sheet = app.named("Character sheet").queryComponent().getExtents(pyatspi.DESKTOP_COORDS)
            transcript = app.named("Story transcript").queryComponent().getExtents(pyatspi.DESKTOP_COORDS)
            return sheet.y >= transcript.y + transcript.height - 1
        app.wait(sheet_stacked, "character sheet reflows below transcript")
        app.choose("Next passage")
        app.wait(lambda: "The new passage is visible." in text(app.latest()), "long transcript advances")
        app.wait(lambda: app.latest().getState().contains(pyatspi.STATE_FOCUSED), "new passage focused")

        def passage_in_window():
            passage = app.latest().queryComponent().getExtents(pyatspi.DESKTOP_COORDS)
            bounds = window.queryComponent().getExtents(pyatspi.DESKTOP_COORDS)
            return passage.y >= bounds.y and passage.y + passage.height <= bounds.y + bounds.height
        app.wait(passage_in_window, "new passage scrolled inside the real native window")
        app.click("Library")
        app.click("Back to Player Test Continue where you left off")
        app.wait(lambda: app.latest().getState().contains(pyatspi.STATE_FOCUSED), "resume restores focus")
        app.click("Restart")
        app.wait(lambda: "Long beginning" in text(app.latest()), "restart returns to first frame")

    def test_keyboard_choices(self):
        app = self.launch(self.fixtures / "linear.zip")
        app.wait(lambda: app.latest().getState().contains(pyatspi.STATE_FOCUSED), "initial focus")
        app.key("Tab")
        app.key("Return")
        app.wait(lambda: "Second passage text." in text(app.latest()), "keyboard choice")
        app.wait(lambda: app.latest().getState().contains(pyatspi.STATE_FOCUSED), "next focus")
        app.key("Tab")
        app.key("space")
        app.wait(lambda: "Finished safely." in text(app.latest()), "keyboard Continue")


def scenario_test(scenario):
    def run(self):
        app = self.launch(self.fixtures / (scenario["Fixture"] + ".zip"))
        for step in scenario["Steps"]:
            if step["Choose"] is not None:
                app.choose(step["Choose"])
            app.observe(step)
    return run


def invalid_upload_test(kind, expected):
    def run(self):
        app = self.launch(self.fixtures / "linear.zip")
        app.choose("Walk onward")
        app.click("Library")
        app.upload(self.fixtures / f"invalid-{kind}.zip")
        app.wait(lambda: expected in text(app.root()), f"invalid {kind} upload error", timeout=45)
        app.click("Back to Player Test Continue where you left off")
        app.wait(lambda: "Second passage text." in text(app.latest()), "failed upload preserves active story")
        app.click("Library")
        app.upload(self.fixtures / "custom.zip")
        app.wait(lambda: "Alex's quiet story." in text(app.latest()), "recover after invalid upload")
    return run


def invalid_cli_test(kind, expected):
    def run(self):
        path = self.fixtures / f"invalid-{kind}.zip"
        app = self.launch(path)
        app.wait(lambda: expected in text(app.root()), f"invalid CLI {kind} error", timeout=45)
        app.open_bundled()
        app.latest()
    return run


@contextmanager
def window_manager(output):
    output.mkdir(parents=True, exist_ok=True)
    with (output / "window-manager.log").open("w") as log:
        manager = subprocess.Popen(["xfwm4", "--compositor=off", "--sm-client-disable"],
                                   stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 10
            while True:
                if manager.poll() is not None:
                    raise RuntimeError(f"Window manager exited: {output / 'window-manager.log'}")
                status = subprocess.run(["wmctrl", "-m"], capture_output=True, text=True, check=False)
                if status.returncode == 0 and "Xfwm4" in status.stdout:
                    break
                if time.monotonic() >= deadline:
                    raise RuntimeError("Private Xfwm4 window manager did not become ready")
                time.sleep(0.1)
            yield
        finally:
            if manager.poll() is None:
                manager.terminate()
                try:
                    manager.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    manager.kill()
                    manager.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--fixtures", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--filter", help="Run test names containing this string")
    args = parser.parse_args()
    with window_manager(args.output.resolve()):
        return run_suite(args, parser)


def run_suite(args, parser):
    PlayerTests.executable = args.executable.resolve(strict=True)
    PlayerTests.fixtures = args.fixtures.resolve(strict=True)
    PlayerTests.output = args.output.resolve()
    if args.probe:
        suite = unittest.TestSuite([PlayerTests("test_accessibility_probe")])
    else:
        for scenario in json.loads((PlayerTests.fixtures / "scenarios.json").read_text()):
            setattr(PlayerTests, "test_scenario_" + scenario["Id"].replace("-", "_"), scenario_test(scenario))
        for kind, expected in json.loads((PlayerTests.fixtures / "errors.json").read_text()).items():
            setattr(PlayerTests, "test_upload_" + kind.replace("-", "_"), invalid_upload_test(kind, expected))
            if kind in ("zip", "manifest", "yaml", "tale"):
                cli_expected = "Couldn't open" if kind == "zip" else expected
                setattr(PlayerTests, "test_cli_" + kind, invalid_cli_test(kind, cli_expected))
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(PlayerTests)
    if args.filter:
        suite = unittest.TestSuite(test for test in suite if args.filter in test.id())
        if suite.countTestCases() == 0:
            parser.error(f"No native tests matched {args.filter!r}")

    class RecordedResult(unittest.TextTestResult):
        def __init__(self, *arguments, **keywords):
            super().__init__(*arguments, **keywords)
            self.cases = []
            self.started = 0

        def startTest(self, test):
            self.started = time.monotonic()
            super().startTest(test)

        def stopTest(self, test):
            self.cases.append((test.id(), time.monotonic() - self.started))
            super().stopTest(test)

    result = unittest.TextTestRunner(verbosity=2, resultclass=RecordedResult).run(suite)
    report = ET.Element("testsuite", name="Photino Linux", tests=str(result.testsRun),
                        failures=str(len(result.failures)), errors=str(len(result.errors)))
    failures = {test.id(): trace for test, trace in result.failures}
    errors = {test.id(): trace for test, trace in result.errors}
    for name, duration in result.cases:
        case = ET.SubElement(report, "testcase", name=name, time=f"{duration:.3f}")
        if name in failures:
            ET.SubElement(case, "failure").text = failures[name]
        if name in errors:
            ET.SubElement(case, "error").text = errors[name]
    PlayerTests.output.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(report).write(PlayerTests.output / "results.xml", encoding="utf-8", xml_declaration=True)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
