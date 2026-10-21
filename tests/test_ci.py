"""Regression checks for release identity, package boundaries, and Pages paths."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from zipfile import ZipFile, ZipInfo

SCRIPTS = Path(__file__).resolve().parents[1] / ".github/scripts"
sys.path.insert(0, str(SCRIPTS))
from package_version import version_from_tag
from publish_samples import normalize_base_path
from verify_packages import ENGINE_ASSEMBLIES, verify, verify_desktop_archive


class ReleaseVersionTests(unittest.TestCase):
    def test_supported_versions(self):
        for version in ["0.1.0", "1.2.3", "1.0.0-preview.1", "1.0.0-rc.0",
                        "1.0.0-alpha-beta.12", "1.0.0-001a"]:
            with self.subTest(version=version):
                self.assertEqual(version_from_tag("v" + version), version)

    def test_invalid_tags_fail(self):
        for tag in ["1.2.3", "v1.2", "v01.2.3", "v1.02.3", "v1.2.03",
                    "v1.2.3-", "v1.2.3-a..1", "v1.2.3-01", "v1.2.3-a.00",
                    "v1.2.3+build.1", "v1.2.3\n", "v1.2.3/extra", "v1.2.3-$(echo bad)"]:
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                version_from_tag(tag)

    def test_cli_errors_are_nonzero_not_fallback_versions(self):
        for arguments in [["--tag", "v1"], ["--ci", "0"], ["--ci", "-1"]]:
            result = subprocess.run([sys.executable, SCRIPTS / "package_version.py", *arguments],
                                    capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, "")
            self.assertIn("error:", result.stderr)

    def test_ci_version_is_explicitly_prerelease(self):
        result = subprocess.run([sys.executable, SCRIPTS / "package_version.py", "--ci", "42"],
                                capture_output=True, text=True, check=True)
        self.assertEqual(result.stdout.strip(), "0.0.0-ci.42")


class PagesPathTests(unittest.TestCase):
    def test_root_and_subfolder(self):
        for value, expected in [("", "/"), ("/", "/"), ("/talewright", "/talewright/"),
                                ("/talewright/", "/talewright/")]:
            self.assertEqual(normalize_base_path(value), expected)

    def test_invalid_paths_fail(self):
        for value in ["relative", "//host", "/../repo", "/./repo", "/repo?query", '/repo"><script>']:
            with self.subTest(value=value), self.assertRaises(ValueError):
                normalize_base_path(value)


class PackageBoundaryTests(unittest.TestCase):
    def write_package(self, directory, identity, version="1.2.3", dependency="1.2.3"):
        path = directory / f"{identity}.{version}.nupkg"
        dependencies = (f'<dependencies><group><dependency id="Talewright" version="{dependency}" />'
                        '</group></dependencies>') if identity.endswith(".Player") else ""
        with ZipFile(path, "w") as package:
            package.writestr(f"{identity}.nuspec", f"""<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
<metadata><id>{identity}</id><version>{version}</version>{dependencies}</metadata></package>""")
            package.writestr("README.md", "Package readme")
            if identity == "Talewright.Cli":
                prefix = "tools/net10.0/any/"
                for name in ["Talewright.Cli.dll", "Talewright.Cli.deps.json",
                             "Talewright.Cli.runtimeconfig.json",
                             "publish-assets/web/Talewright.Host.Web.csproj",
                             "publish-assets/desktop/Talewright.Host.Desktop.csproj",
                             "publish-assets/player/talewright.css",
                             "publish-assets/player/Components/StoryPlayer.razor.js",
                             "publish-assets/player/Saves/IndexedDbSaveStore.js",
                             "publish-assets/launchers/start.sh",
                             "publish-assets/launchers/start.command",
                             "publish-assets/launchers/start.cmd",
                             "publish-assets/ENGINE-LICENSE.txt"]:
                    package.writestr(prefix + name, "")
                package.writestr(prefix + "DotnetToolSettings.xml",
                                 '<DotNetCliTool><Commands><Command Name="talewright" '
                                 'EntryPoint="Talewright.Cli.dll" Runner="dotnet" /></Commands></DotNetCliTool>')
                for assembly in ENGINE_ASSEMBLIES | {"Talewright.Player"}:
                    package.writestr(prefix + assembly + ".dll", "")
            else:
                package.writestr(f"lib/net10.0/{identity}.dll", b"test placeholder")
            if identity == "Talewright":
                for assembly in ENGINE_ASSEMBLIES:
                    for extension in ("dll", "xml"):
                        package.writestr(f"lib/net10.0/{assembly}.{extension}", b"test placeholder")
            if identity.endswith(".Player"):
                for name in ["staticwebassets/talewright.css",
                             "staticwebassets/Components/StoryPlayer.razor.js",
                             "staticwebassets/Saves/IndexedDbSaveStore.js",
                             "buildTransitive/Talewright.Player.props"]:
                    package.writestr(name, "")
        return path

    def test_exactly_two_libraries_and_tool(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            self.write_package(directory, "Talewright")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")
            self.write_package(directory, "Talewright.Player")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")
            self.write_package(directory, "Talewright.Cli")
            verify(directory, "1.2.3")
            self.write_package(directory, "Talewright.Host.Desktop")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")

    def test_mismatched_package_or_dependency_versions_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            self.write_package(directory, "Talewright")
            self.write_package(directory, "Talewright.Cli")
            player = self.write_package(directory, "Talewright.Player", dependency="1.0.0")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")
            player.unlink()
            self.write_package(directory, "Talewright.Player", version="1.2.4")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")

    def test_missing_player_asset_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            self.write_package(directory, "Talewright")
            self.write_package(directory, "Talewright.Cli")
            player = self.write_package(directory, "Talewright.Player")
            with ZipFile(player) as package:
                entries = {name: package.read(name) for name in package.namelist()
                           if name != "staticwebassets/talewright.css"}
            with ZipFile(player, "w") as package:
                for name, content in entries.items():
                    package.writestr(name, content)
            with self.assertRaisesRegex(ValueError, "missing Razor asset"):
                verify(directory, "1.2.3")

    def test_missing_component_assembly_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            engine = self.write_package(directory, "Talewright")
            self.write_package(directory, "Talewright.Cli")
            self.write_package(directory, "Talewright.Player")
            with ZipFile(engine) as package:
                entries = {name: package.read(name) for name in package.namelist()
                           if name != "lib/net10.0/Talewright.Narrative.dll"}
            with ZipFile(engine, "w") as package:
                for name, content in entries.items():
                    package.writestr(name, content)
            with self.assertRaisesRegex(ValueError, "missing engine assembly asset"):
                verify(directory, "1.2.3")

    def test_missing_tool_entry_point_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            self.write_package(directory, "Talewright")
            self.write_package(directory, "Talewright.Player")
            tool = self.write_package(directory, "Talewright.Cli")
            with ZipFile(tool) as package:
                entries = {name: package.read(name) for name in package.namelist()
                           if not name.endswith("DotnetToolSettings.xml")}
            with ZipFile(tool, "w") as package:
                for name, content in entries.items():
                    package.writestr(name, content)
            with self.assertRaisesRegex(ValueError, "missing tools/"):
                verify(directory, "1.2.3")


class DesktopArchiveTests(unittest.TestCase):
    def write_archive(self, path, *, missing=None, executable=True, self_contained=True):
        files = {name: "" for name in ["start.sh", "start.command", "start.cmd",
                                       "ENGINE-LICENSE.txt", "DEPLOYMENT.txt"]}
        for os_name, native, runtime in [("linux", "Photino.Native.so", "libcoreclr.so"),
                                        ("win", "Photino.Native.dll", "coreclr.dll"),
                                        ("osx", "Photino.Native.dylib", "libcoreclr.dylib")]:
            for cpu in ["x64", "arm64"]:
                prefix = f"{os_name}-{cpu}/"
                for name in ["Talewright.Host.Desktop" + (".exe" if os_name == "win" else ""),
                             "Talewright.Host.Desktop.dll", native, runtime, "wwwroot/index.html",
                             "wwwroot/_content/Talewright.Player/talewright.css",
                             "wwwroot/_content/Talewright.Player/Components/StoryPlayer.razor.js",
                             "wwwroot/_content/Talewright.Player/Saves/IndexedDbSaveStore.js"]:
                    files[prefix + name] = ""
                if os_name == "win":
                    files[prefix + "WebView2Loader.dll"] = ""
                framework = {"name": "Microsoft.NETCore.App", "version": "10.0.13"}
                options = {"includedFrameworks": [framework]} if self_contained else {"framework": framework}
                files[prefix + "Talewright.Host.Desktop.runtimeconfig.json"] = json.dumps({"runtimeOptions": options})
        with ZipFile(path, "w") as archive:
            for name, content in files.items():
                if name != missing:
                    entry = ZipInfo(name)
                    entry.external_attr = (0o100755 if executable else 0o100644) << 16
                    archive.writestr(entry, content)

    def test_requires_all_six_self_contained_builds_and_executable_launchers(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "desktop.zip"
            self.write_archive(path)
            verify_desktop_archive(path)
            for change, error in [
                    ({"missing": "win-arm64/coreclr.dll"}, "missing"),
                    ({"self_contained": False}, "installed .NET"),
                    ({"executable": False}, "executable permissions")]:
                with self.subTest(change=change):
                    self.write_archive(path, **change)
                    with self.assertRaisesRegex(ValueError, error):
                        verify_desktop_archive(path)


if __name__ == "__main__":
    unittest.main()
