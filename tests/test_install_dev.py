"""Local development installer orchestration and failure contracts."""

import argparse
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "install_dev", Path(__file__).resolve().parents[1] / "tooling/install-dev.py")
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)


class InstallDevTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="local dev ")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.editor = self.root / "tooling/vscode-talewright"
        self.editor.mkdir(parents=True)
        self.args = argparse.Namespace(code="code-insiders", profile="Dev Profile",
                                       bin_dir=self.root / "local bin")
        self.commands = []
        self.failure = None
        for patcher in [
            patch.object(installer, "ROOT", self.root),
            patch.object(installer.platform, "system", return_value="Linux"),
            patch.object(installer.platform, "machine", return_value="x86_64"),
            patch.object(installer.shutil, "which", return_value="/usr/bin/tool"),
            patch.dict(os.environ, XDG_DATA_HOME=str(self.root / "data"),
                       TALEWRIGHT_RELEASE_TAG="v1.0.0"),
            patch.object(installer, "run", side_effect=self.run_command),
        ]:
            patcher.start()
            self.addCleanup(patcher.stop)

    def run_command(self, command, **options):
        self.commands.append((command, options))
        if self.failure and self.failure(command):
            raise subprocess.CalledProcessError(1, command)
        if command[:2] == ["dotnet", "publish"]:
            output = Path(command[command.index("-o") + 1])
            output.mkdir(parents=True, exist_ok=True)
            name = "Talewright.Debugger" if "debugger" in command[-3] else "Talewright.Cli"
            (output / name).write_text("fresh executable")
        if command == ["npm", "run", "package"]:
            target = options["env"]["TALEWRIGHT_VSIX_TARGET"]
            name = "talewright.vsix" if target == "linux-x64" else f"talewright-{target}.vsix"
            (self.editor / "artifacts").mkdir(exist_ok=True)
            (self.editor / "artifacts" / name).write_text("fresh VSIX")

    @property
    def home(self):
        return self.root / "data/talewright/dev"

    def test_installs_checkout_and_replaces_previous_build(self):
        installer.install(self.args)
        first = (self.home / "current").resolve()
        shim = self.args.bin_dir / "talewright"
        self.assertEqual(shim.read_text(), "fresh executable")
        self.assertTrue((first / "debug-host/Talewright.Debugger").is_file())
        installer.install(self.args)
        self.assertFalse(first.exists())
        self.assertEqual(shim.read_text(), "fresh executable")
        publishes = [(command, options) for command, options in self.commands
                     if command[:2] == ["dotnet", "publish"]]
        self.assertEqual(len(publishes), 4)
        for command, options in publishes:
            self.assertIn("linux-x64", command)
            self.assertIn("-p:PackAsTool=false", command)
            self.assertEqual(options["env"]["MSBUILDDISABLENODEREUSE"], "1")
            self.assertNotIn("TALEWRIGHT_RELEASE_TAG", options["env"])
        installs = [command for command, _ in self.commands if "--install-extension" in command]
        self.assertEqual(installs[0], ["code-insiders", "--install-extension",
                                      str(self.editor / "artifacts/talewright.vsix"),
                                      "--force", "--profile", "Dev Profile"])

    def test_supported_hosts_use_matching_cli_and_vsix(self):
        for system, machine, rid, target in [
            ("Linux", "x86_64", "linux-x64", "linux-x64"),
            ("Linux", "aarch64", "linux-arm64", "linux-arm64"),
            ("Darwin", "x86_64", "osx-x64", "darwin-x64"),
            ("Darwin", "arm64", "osx-arm64", "darwin-arm64"),
        ]:
            with self.subTest(target=target), \
                 patch.object(installer.platform, "system", return_value=system), \
                 patch.object(installer.platform, "machine", return_value=machine):
                self.commands.clear()
                installer.install(self.args)
                publishes = [command for command, _ in self.commands
                             if command[:2] == ["dotnet", "publish"]]
                self.assertTrue(all(rid in command for command in publishes))
                install = next(command for command, _ in self.commands
                               if "--install-extension" in command)
                name = "talewright.vsix" if target == "linux-x64" else f"talewright-{target}.vsix"
                self.assertIn(str(self.editor / "artifacts" / name), install)

    def test_build_package_check_and_editor_failures_preserve_old_cli(self):
        for failing in ("publish", "package", "test:package", "--install-extension"):
            with self.subTest(failing=failing):
                self.failure = None
                installer.install(self.args)
                previous = (self.home / "current").resolve()
                self.failure = lambda command: failing in command
                with self.assertRaises(subprocess.CalledProcessError):
                    installer.install(self.args)
                self.assertEqual((self.home / "current").resolve(), previous)
                self.assertEqual(list(self.home.glob("build-*")), [previous])

    def test_refuses_to_overwrite_unmanaged_files(self):
        self.args.bin_dir.mkdir()
        shim = self.args.bin_dir / "talewright"
        shim.write_text("existing CLI")
        with self.assertRaisesRegex(RuntimeError, "Refusing to replace"):
            installer.install(self.args)
        self.assertEqual(shim.read_text(), "existing CLI")
        self.assertFalse(any("publish" in command for command, _ in self.commands))

    def test_missing_tools_and_unsupported_hosts_fail_before_install(self):
        with patch.object(installer.shutil, "which", return_value=None):
            with self.assertRaisesRegex(RuntimeError, "Required command not found"):
                installer.install(self.args)
        with patch.object(installer.platform, "system", return_value="Windows"):
            with self.assertRaisesRegex(RuntimeError, "supports Linux and macOS"):
                installer.install(self.args)
        self.assertEqual(self.commands, [])

    def test_refuses_unmanaged_installation_symlink(self):
        self.home.mkdir(parents=True)
        unrelated = self.root / "other installation"
        unrelated.mkdir()
        (unrelated / "keep.txt").write_text("keep")
        (self.home / "current").symlink_to(unrelated)
        with self.assertRaisesRegex(RuntimeError, "unmanaged installation"):
            installer.install(self.args)
        self.assertEqual((unrelated / "keep.txt").read_text(), "keep")
        self.assertFalse(any("publish" in command for command, _ in self.commands))

    def test_existing_editor_dependencies_skip_npm_ci(self):
        for name in ("node_modules/.bin/esbuild", "node_modules/@vscode/vsce/package.json"):
            path = self.editor / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.touch()
        installer.install(self.args)
        self.assertNotIn(["npm", "ci"], [command for command, _ in self.commands])


if __name__ == "__main__":
    unittest.main()
