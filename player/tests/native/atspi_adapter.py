#!/usr/bin/python3
"""Native actions and accessibility observations; test cases and assertions live in C#/xUnit."""

import argparse
from contextlib import contextmanager
import json
from pathlib import Path
import re
import subprocess
import sys
import time
import traceback

import gi

gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib
import pyatspi


def normalized(value):
    return " ".join(value.split())


def nodes(root):
    stack = [(root, 0)]
    while stack:
        node, depth = stack.pop()
        if depth > 40 or node.getState().contains(pyatspi.STATE_DEFUNCT):
            continue
        yield node
        count = node.childCount
        if count >= 0:
            stack.extend((child, depth + 1) for child in
                         (node.getChildAtIndex(index) for index in reversed(range(count))) if child is not None)


class Desktop:
    def __init__(self, executable, output, paths):
        self.output = output
        output.mkdir(parents=True, exist_ok=True)
        self.log = (output / "app.log").open("w")
        command = ["dotnet", str(executable)] if executable.suffix == ".dll" else [str(executable)]
        self.process = subprocess.Popen(
            [*command, *paths], cwd=executable.parent,
            stdout=self.log, stderr=subprocess.STDOUT,
        )

    def root(self):
        desktop = pyatspi.Registry.getDesktop(0)
        for application in desktop:
            if application and application.get_process_id() == self.process.pid:
                return application
        return None

    def wait(self, predicate, description):
        deadline = time.monotonic() + 15
        last_error = None
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError(f"Photino exited with {self.process.returncode}: {self.output / 'app.log'}")
            try:
                result = predicate()
                if result:
                    return result
            except GLib.Error as error:
                last_error = error
            time.sleep(0.1)
        raise TimeoutError(f"Native action timed out: {description}; last accessibility error: {last_error}")

    def find(self, name=None, role=None, pattern=None, within=None):
        root = self.root()
        if root is None:
            return None
        if within is not None:
            root = next((node for node in nodes(root) if normalized(node.name) == within), None)
            if root is None:
                return None
        return next((node for node in nodes(root)
                     if (name is None or normalized(node.name) == name)
                     and (role is None or node.getRoleName() == role)
                     and (pattern is None or re.fullmatch(pattern, normalized(node.name)))), None)

    def click(self, **query):
        node = self.wait(lambda: self.find(**query), f"activate {query}")
        actions = node.queryAction()
        if actions.nActions == 0 or not actions.doAction(0):
            raise RuntimeError(f"Control does not support activation: {query}")

    def key(self, key, event=pyatspi.KEY_PRESSRELEASE):
        key = {"Enter": "Return", "Space": "space"}.get(key, key)
        found, entries = Gdk.Keymap.get_for_display(Gdk.Display.get_default()).get_entries_for_keyval(
            Gdk.keyval_from_name(key))
        if not found:
            raise RuntimeError(f"No hardware keycode for {key}")
        pyatspi.Registry.generateKeyboardEvent(entries[0].keycode, None, event)

    def window_id(self):
        windows = subprocess.check_output(["wmctrl", "-lp"], text=True).splitlines()
        window = next((line.split()[0] for line in windows
                       if len(line.split()) >= 3 and line.split()[2] == str(self.process.pid)), None)
        if window is None:
            raise RuntimeError("No window belongs to the Photino process")
        return window

    def upload(self, path):
        Path(path).resolve(strict=True)
        self.click(name="Story archive", role="push button")
        self.wait(lambda: self.find(role="file chooser"), "file chooser")
        self.key("Control_L", pyatspi.KEY_PRESS)
        try:
            self.key("l")
        finally:
            self.key("Control_L", pyatspi.KEY_RELEASE)

        def location():
            root = self.root()
            return None if root is None else next((node for node in nodes(root)
                if node.getRoleName() == "text"
                and node.getState().contains(pyatspi.STATE_EDITABLE)
                and node.getState().contains(pyatspi.STATE_FOCUSED)), None)
        entry = self.wait(location, "editable file chooser location")
        entry.queryEditableText().setTextContents(path)
        actions = entry.queryAction()
        activate = next((index for index in range(actions.nActions)
                         if actions.getName(index) == "activate"), None)
        if activate is None or not actions.doAction(activate):
            raise RuntimeError("The GTK location entry does not support activation")
        self.wait(lambda: self.find(role="file chooser") is None, "file chooser accepts selected archive")

    def snapshot(self):
        root = self.root()
        if root is None:
            return {"name": "", "role": "", "text": "", "focused": False,
                    "bounds": {"x": 0, "y": 0, "width": 0, "height": 0}, "children": []}

        def serialize(node, depth=0):
            if depth > 40 or node.getState().contains(pyatspi.STATE_DEFUNCT):
                return None
            content = ""
            try:
                content = node.queryText().getText(0, -1)
            except NotImplementedError:
                pass
            bounds = {"x": 0, "y": 0, "width": 0, "height": 0}
            try:
                rectangle = node.queryComponent().getExtents(pyatspi.DESKTOP_COORDS)
                bounds = dict(zip(bounds, rectangle))
            except NotImplementedError:
                pass
            count = node.childCount
            children = []
            for index in range(max(0, count)):
                child = node.getChildAtIndex(index)
                if child is not None:
                    data = serialize(child, depth + 1)
                    if data is not None:
                        children.append(data)
            return {"name": normalized(node.name), "role": node.getRoleName(), "text": normalized(content),
                    "focused": node.getState().contains(pyatspi.STATE_FOCUSED),
                    "bounds": bounds, "children": children}
        return serialize(root)

    def close(self):
        try:
            (self.output / "accessibility.json").write_text(json.dumps(self.snapshot(), indent=2))
            window = Gdk.get_default_root_window()
            screenshot = Gdk.pixbuf_get_from_window(window, 0, 0, window.get_width(), window.get_height())
            if screenshot is None:
                raise RuntimeError("Could not capture native display")
            screenshot.savev(str(self.output / "screen.png"), "png", [], [])
        finally:
            if self.process.poll() is None:
                self.process.terminate()
                try:
                    self.process.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=8)
            self.log.close()


def serve(args):
    app = None
    try:
        for line in sys.stdin:
            try:
                request = json.loads(line)
                command = request["command"]
                arguments = request.get("arguments") or {}
                result = None
                if command == "launch":
                    if app is not None:
                        raise RuntimeError("An app is already running")
                    app = Desktop(args.executable.resolve(strict=True), args.output, arguments["paths"])
                elif app is None:
                    raise RuntimeError("Launch the app first")
                elif command == "snapshot":
                    result = app.wait(app.snapshot, "read accessibility tree")
                elif command == "click":
                    app.click(**arguments)
                elif command == "activate":
                    node = app.wait(lambda: app.find(name=arguments["name"]), "keyboard activation target")
                    node.queryComponent().grabFocus()
                    app.key("Space")
                elif command == "fill":
                    node = app.wait(lambda: app.find(name=arguments["name"]), "editable named field")
                    node.queryComponent().grabFocus()
                    app.key("Control_L", pyatspi.KEY_PRESS)
                    try:
                        app.key("a")
                    finally:
                        app.key("Control_L", pyatspi.KEY_RELEASE)
                    pyatspi.Registry.generateKeyboardEvent(0, arguments["value"], pyatspi.KEY_STRING)
                    app.wait(lambda: node.queryText().getText(0, -1) == arguments["value"],
                             "finish typing the named field")
                    app.key("Tab")
                elif command == "upload":
                    app.upload(**arguments)
                elif command == "key":
                    app.key(**arguments)
                elif command == "resize":
                    subprocess.run(["wmctrl", "-ir", app.window_id(), "-e",
                                    f"0,-1,-1,{arguments['width']},{arguments['height']}"], check=True)
                elif command == "close-window":
                    subprocess.run(["wmctrl", "-ic", app.window_id()], check=True)
                    result = app.process.wait(timeout=20)
                elif command == "close":
                    try:
                        app.close()
                    finally:
                        app = None
                else:
                    raise ValueError(f"Unknown command: {command}")
                print(json.dumps({"ok": True, "result": result}), flush=True)
                if command == "close":
                    return 0
            except Exception:
                # The RPC boundary reports the full failure to the C# runner, never a success-shaped default.
                print(json.dumps({"ok": False, "error": traceback.format_exc()}), flush=True)
                return 1
    finally:
        if app is not None:
            app.close()
    return 0


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
                    raise TimeoutError("Private Xfwm4 window manager did not become ready")
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
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="mode", required=True)
    adapter = subcommands.add_parser("adapter")
    adapter.add_argument("--executable", type=Path, required=True)
    adapter.add_argument("--output", type=Path, required=True)
    session = subcommands.add_parser("session")
    session.add_argument("--output", type=Path, required=True)
    session.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.mode == "adapter":
        return serve(args)
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("session requires a command after --")
    with window_manager(args.output):
        return subprocess.run(command, check=False).returncode


if __name__ == "__main__":
    sys.exit(main())
