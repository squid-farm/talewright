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
                                       bin_dir=self.root / "local bin", global_tool=False,
                                       publish_templates=False)
        self.commands = []
        self.failure = None
        for patcher in [
            patch.object(installer, "ROOT", self.root),
            patch.object(installer.platform, "system", return_value="Linux"),
            patch.object(installer.platform, "machine", return_value="x86_64"),
            patch.object(installer.shutil, "which", return_value="/usr/bin/tool"),
            patch.dict(os.environ, XDG_DATA_HOME=str(self.root / "data"),
                       DOTNET_CLI_HOME=str(self.root / "dotnet home"),
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
        if command[:3] == ["dotnet", "tool", "install"]:
            directory = Path(command[command.index("--tool-path") + 1])
            directory.mkdir(parents=True)
            (directory / "talewright").write_text("staged global tool")
        if command[:3] == ["dotnet", "tool", "update"]:
            shim = self.root / "dotnet home/.dotnet/tools/talewright"
            shim.parent.mkdir(parents=True, exist_ok=True)
            shim.write_text("fresh global tool")
        if command[0] == "bash" and command[1].endswith("package_publish_template.sh"):
            version, target = command[2:4]
            runtime = command[4] if len(command) > 4 else None
            output = Path(options["env"]["TALEWRIGHT_PUBLISH_TEMPLATE_OUTPUT"])
            staging = output / f"build-{target}{'-' + runtime if runtime else ''}"
            staging.mkdir(parents=True)
            name = f"talewright-publish-template-{target}{'-' + runtime if runtime else ''}-{version}.zip"
            for suffix in ("", ".sha256"):
                (output / (name + suffix)).write_text("template")

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

    def test_macos_publish_templates_use_dotnet_application_support_cache(self):
        self.args.publish_templates = True
        home = self.root / "mac home"
        with patch.object(installer.platform, "system", return_value="Darwin"), \
             patch.dict(os.environ, HOME=str(home), XDG_DATA_HOME=str(self.root / "wrong data")):
            installer.install(self.args)
        templates = [command for command, _ in self.commands if command[0] == "bash"]
        version = templates[0][2]
        assets = home / "Library/Application Support/Talewright/publish-templates/development-assets" / version
        self.assertEqual(len(list(assets.iterdir())), 14)
        self.assertFalse((self.root / "wrong data/Talewright/publish-templates").exists())

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

    def test_global_tool_replaces_at_existing_path_with_unique_local_packages(self):
        self.args.global_tool = True
        shim = self.root / "dotnet home/.dotnet/tools/talewright"
        shim.parent.mkdir(parents=True)
        shim.write_text("released tool")
        for _ in range(2):
            installer.install(self.args)
            self.assertEqual(shim.read_text(), "fresh global tool")
            self.assertEqual(list(self.home.glob("build-*")), [])
        updates = [command for command, _ in self.commands
                   if command[:3] == ["dotnet", "tool", "update"]]
        self.assertEqual(len(updates), 2)
        versions = [command[command.index("--version") + 1] for command in updates]
        self.assertNotEqual(versions[0], versions[1])
        for command in updates:
            self.assertIn("--global", command)
            self.assertIn("--allow-downgrade", command)
            self.assertIn("--no-http-cache", command)
            self.assertIn("--source", command)
            self.assertNotIn("--add-source", command)
        self.assertFalse(any("publish" in command for command, _ in self.commands))
        self.assertFalse(self.args.bin_dir.exists())
        for update in updates:
            version = update[update.index("--version") + 1]
            pack = next(command for command, _ in self.commands if f"-p:Version={version}" in command)
            self.assertEqual(pack[:2], ["dotnet", "pack"])
        last = [command for command, _ in self.commands]
        self.assertLess(last.index(["npm", "run", "test:package"]), last.index(updates[0]))

    def test_global_tool_failures_do_not_uninstall_existing_tool(self):
        self.args.global_tool = True
        shim = self.root / "dotnet home/.dotnet/tools/talewright"
        shim.parent.mkdir(parents=True)
        for failing in ("pack", "install", "test:package", "--install-extension", "update"):
            with self.subTest(failing=failing):
                self.commands.clear()
                shim.write_text("released tool")
                self.failure = lambda command: failing in command
                with self.assertRaises(subprocess.CalledProcessError):
                    installer.install(self.args)
                self.assertEqual(shim.read_text(), "released tool")
                self.assertEqual(list(self.home.glob("build-*")), [])
                self.assertFalse(any("uninstall" in command for command, _ in self.commands))

    def test_opt_in_prepares_matching_templates_for_both_install_modes(self):
        self.args.publish_templates = True
        versions = []
        for global_tool in (False, True):
            with self.subTest(global_tool=global_tool):
                self.args.global_tool = global_tool
                self.commands.clear()
                installer.install(self.args)
                templates = [command for command, _ in self.commands if command[0] == "bash"]
                self.assertEqual(len(templates), 7)
                version = templates[0][2]
                versions.append(version)
                self.assertTrue(all(command[2] == version for command in templates))
                self.assertEqual(
                    {(command[3], command[4] if len(command) > 4 else None) for command in templates},
                    {("web", None), *(("desktop", runtime) for runtime in installer.DESKTOP_RUNTIMES)})
                assets = self.root / "data/Talewright/publish-templates/development-assets" / version
                self.assertEqual(len(list(assets.iterdir())), 14)
                if global_tool:
                    install = next(command for command, _ in self.commands
                                   if command[:3] == ["dotnet", "tool", "update"])
                    self.assertIn(version, install)
                else:
                    publish = next(command for command, _ in self.commands
                                   if command[:2] == ["dotnet", "publish"])
                    self.assertIn(f"-p:Version={version}", publish)
                    self.assertFalse(((self.home / "current").resolve() / "template-build").exists())
        self.assertNotEqual(*versions)

    def test_template_failure_preserves_existing_installation(self):
        self.args.publish_templates = True
        for global_tool in (False, True):
            with self.subTest(global_tool=global_tool):
                self.args.global_tool = global_tool
                self.failure = None
                installer.install(self.args)
                assets = self.root / "data/Talewright/publish-templates/development-assets"
                before = set(assets.iterdir())
                shim = (self.root / "dotnet home/.dotnet/tools/talewright"
                        if global_tool else self.args.bin_dir / "talewright")
                old = shim.read_text()
                self.failure = lambda command: command[0] == "bash" and "win-x64" in command
                with self.assertRaises(subprocess.CalledProcessError):
                    installer.install(self.args)
                self.assertEqual(shim.read_text(), old)
                self.assertEqual(set(assets.iterdir()), before)

    def test_later_install_failure_removes_uninstalled_templates(self):
        self.args.publish_templates = True
        self.failure = lambda command: "--install-extension" in command
        with self.assertRaises(subprocess.CalledProcessError):
            installer.install(self.args)
        assets = self.root / "data/Talewright/publish-templates/development-assets"
        self.assertEqual(list(assets.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
