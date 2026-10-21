"""Regression checks for release identity, package boundaries, and Pages paths."""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from zipfile import ZipFile

SCRIPTS = Path(__file__).resolve().parents[1] / ".github/scripts"
sys.path.insert(0, str(SCRIPTS))
from package_version import version_from_tag
from publish_samples import normalize_base_path
from verify_packages import verify


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
            package.writestr(f"lib/net10.0/{identity}.dll", b"test placeholder")
            if identity.endswith(".Player"):
                for name in ["staticwebassets/talewright.css",
                             "staticwebassets/Components/StoryPlayer.razor.js",
                             "buildTransitive/Talewright.Player.props"]:
                    package.writestr(name, "")
        return path

    def test_exactly_two_packages(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            self.write_package(directory, "Talewright")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")
            self.write_package(directory, "Talewright.Player")
            verify(directory, "1.2.3")
            self.write_package(directory, "Talewright.Host.Desktop")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")

    def test_mismatched_package_or_dependency_versions_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            self.write_package(directory, "Talewright")
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
            player = self.write_package(directory, "Talewright.Player")
            with ZipFile(player) as package:
                entries = {name: package.read(name) for name in package.namelist()
                           if name != "staticwebassets/talewright.css"}
            with ZipFile(player, "w") as package:
                for name, content in entries.items():
                    package.writestr(name, content)
            with self.assertRaisesRegex(ValueError, "missing Razor asset"):
                verify(directory, "1.2.3")


if __name__ == "__main__":
    unittest.main()
