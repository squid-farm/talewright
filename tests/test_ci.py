"""Regression checks for release identity, package boundaries, and Pages paths."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from zipfile import ZipFile, ZipInfo

SCRIPTS = Path(__file__).resolve().parents[1] / ".github/scripts"
sys.path.insert(0, str(SCRIPTS))
from package_version import version_from_tag
from publish_samples import normalize_base_path
from verify_packages import ENGINE_ASSEMBLIES, verify, verify_desktop_archive
import ui_test_image_plan


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


class RepositoryLayoutTests(unittest.TestCase):
    ROOT = Path(__file__).resolve().parents[1]

    def test_engine_package_uses_the_Talemark_compiler_identity(self):
        self.assertIn("Talewright.Talemark", ENGINE_ASSEMBLIES)
        self.assertEqual(6, len(ENGINE_ASSEMBLIES))

    def test_both_hosts_embed_all_bundled_story_files(self):
        samples = self.ROOT / "samples"
        expected = {
            path.resolve()
            for path in samples.rglob("*")
            if path.is_file()
            and (path.suffix in {".tale", ".yaml"} or "images" in path.relative_to(samples).parts)
        }
        self.assertTrue(expected)
        for host in ("web", "desktop"):
            with self.subTest(host=host):
                directory = self.ROOT / "apps" / host
                project = ET.parse(next(directory.glob("*.csproj")))
                actual = {
                    path.resolve()
                    for resource in project.findall(".//EmbeddedResource")
                    for pattern in resource.attrib["Include"].replace("\\", "/").split(";")
                    for path in directory.glob(pattern)
                }
                self.assertEqual(expected, actual)

    def test_cli_copies_both_source_hosts_into_publish_templates(self):
        directory = self.ROOT / "tooling/cli/src/Talewright.Cli"
        project = ET.parse(directory / "Talewright.Cli.csproj")
        for host, assembly in (("web", "Talewright.Host.Web"), ("desktop", "Talewright.Host.Desktop")):
            with self.subTest(host=host):
                templates = {
                    path.name
                    for item in project.findall(".//None")
                    if item.attrib.get("Link", "").startswith(f"publish-assets/{host}/")
                    for pattern in item.attrib.get("Include", "").split(";")
                    if pattern
                    for path in directory.glob(pattern.replace("\\", "/"))
                    if path.is_file()
                }
                self.assertTrue({f"{assembly}.csproj", "App.razor", "Program.cs"} <= templates)


class PackageBoundaryTests(unittest.TestCase):
    def write_package(self, directory, identity, version="1.2.3", dependency="1.2.3"):
        path = directory / f"{identity}.{version}.nupkg"
        dependencies = (f'<dependencies><group><dependency id="Talewright" version="{dependency}" />'
                        '</group></dependencies>') if identity.endswith(".Player") else ""
        with ZipFile(path, "w") as package:
            package.writestr(f"{identity}.nuspec", f"""<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
<metadata><id>{identity}</id><version>{version}</version><icon>icon.png</icon>{dependencies}</metadata></package>""")
            package.writestr("README.md", "Package readme")
            package.writestr("icon.png", "")
            if identity == "Talewright.LanguageServer":
                prefix = "tools/net10.0/any/"
                for name in ["Talewright.LanguageServer.dll", "Talewright.LanguageServer.deps.json",
                             "Talewright.LanguageServer.runtimeconfig.json", "Talewright.Authoring.dll",
                             *[f"{assembly}.dll" for assembly in ENGINE_ASSEMBLIES - {"Talewright.Runtime"}]]:
                    package.writestr(prefix + name, "")
                package.writestr(prefix + "DotnetToolSettings.xml",
                                 '<DotNetCliTool><Commands><Command Name="talewright-lsp" '
                                 'EntryPoint="Talewright.LanguageServer.dll" Runner="dotnet" /></Commands></DotNetCliTool>')
            elif identity == "Talewright.Cli":
                prefix = "tools/net10.0/any/"
                for name in ["Talewright.Cli.dll", "Talewright.Cli.deps.json",
                             "Talewright.Cli.runtimeconfig.json",
                             "Talewright.Debugger.dll",
                             "debug-assets/index.html",
                             "debug-assets/debugger.css",
                             "debug-assets/_content/Talewright.Player/talewright.css",
                             "publish-assets/web/Talewright.Host.Web.csproj",
                             "publish-assets/desktop/Talewright.Host.Desktop.csproj",
                             "publish-assets/standalone/web/App.razor",
                             "publish-assets/standalone/web/Program.cs",
                             "publish-assets/standalone/web/wwwroot/index.html",
                             "publish-assets/standalone/desktop/App.razor",
                             "publish-assets/standalone/desktop/Program.cs",
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
                for rid in ["linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64"]:
                    extension = "dll" if rid.startswith("win") else "dylib" if rid.startswith("osx") else "so"
                    package.writestr(prefix + f"runtimes/{rid}/native/Photino.Native.{extension}", "")
            else:
                package.writestr(f"lib/net10.0/{identity}.dll", b"test placeholder")
            if identity == "Talewright":
                package.writestr("buildTransitive/Talewright.targets", "")
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

    def test_exactly_two_libraries_and_two_tools(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            self.write_package(directory, "Talewright")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")
            self.write_package(directory, "Talewright.Player")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")
            self.write_package(directory, "Talewright.Cli")
            with self.assertRaises(ValueError):
                verify(directory, "1.2.3")
            self.write_package(directory, "Talewright.LanguageServer")
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

    def test_runtime_packages_reject_authoring_infrastructure(self):
        for identity in ["Talewright", "Talewright.Player"]:
            for dependency in [None, "Talewright.Authoring", "talewright.authoring", "TALEWRIGHT.AUTHORING",
                               "Talewright.LanguageServer", "talewright.languageserver"]:
                with self.subTest(identity=identity, dependency=dependency), tempfile.TemporaryDirectory() as temporary:
                    directory = Path(temporary)
                    for package_id in ["Talewright", "Talewright.Player", "Talewright.Cli"]:
                        self.write_package(directory, package_id)
                    path = directory / f"{identity}.1.2.3.nupkg"
                    with ZipFile(path) as package:
                        entries = {name: package.read(name) for name in package.namelist()}
                    if dependency is None:
                        entries["lib/net10.0/Talewright.Authoring.dll"] = b"tooling"
                    else:
                        spec = f"{identity}.nuspec"
                        entries[spec] = entries[spec].replace(b"</metadata>", (
                            f'<dependencies><dependency id="{dependency}" version="1.2.3" />'
                            '</dependencies></metadata>').encode())
                    with ZipFile(path, "w") as package:
                        for name, content in entries.items():
                            package.writestr(name, content)
                    with self.assertRaisesRegex(ValueError, "runtime package includes authoring infrastructure"):
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

    def test_language_server_requires_its_authoring_dependency(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            for identity in ["Talewright", "Talewright.Player", "Talewright.Cli", "Talewright.LanguageServer"]:
                self.write_package(directory, identity)
            server = directory / "Talewright.LanguageServer.1.2.3.nupkg"
            with ZipFile(server) as package:
                entries = {name: package.read(name) for name in package.namelist()
                           if name != "tools/net10.0/any/Talewright.Authoring.dll"}
            with ZipFile(server, "w") as package:
                for name, content in entries.items():
                    package.writestr(name, content)
            with self.assertRaisesRegex(ValueError, "missing tools/net10.0/any/Talewright.Authoring.dll"):
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

    def test_missing_debugger_asset_fails(self):
        prefix = "tools/net10.0/any/"
        required = ["Talewright.Debugger.dll", "debug-assets/index.html",
                    "debug-assets/debugger.css",
                    "debug-assets/_content/Talewright.Player/talewright.css"]
        for rid in ["linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64"]:
            extension = "dll" if rid.startswith("win") else "dylib" if rid.startswith("osx") else "so"
            required.append(f"runtimes/{rid}/native/Photino.Native.{extension}")
        for missing in required:
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                self.write_package(directory, "Talewright")
                self.write_package(directory, "Talewright.Player")
                tool = self.write_package(directory, "Talewright.Cli")
                with ZipFile(tool) as package:
                    entries = {name: package.read(name) for name in package.namelist()
                               if name != prefix + missing}
                with ZipFile(tool, "w") as package:
                    for name, content in entries.items():
                        package.writestr(name, content)
                with self.assertRaises(ValueError) as failure:
                    verify(directory, "1.2.3")
                self.assertIn("missing", str(failure.exception))
                self.assertIn(missing if missing in required[:4] else missing.split("/")[1],
                              str(failure.exception))

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


class UiTestImageContracts(unittest.TestCase):
    ROOT = Path(__file__).resolve().parents[1]

    def test_image_metadata_matches_repository_version_pins(self):
        metadata = json.loads((self.ROOT / ".github/docker/ui-tests/image.json").read_text())
        e2e = ET.parse(self.ROOT / "player/tests/Talewright.Player.E2E.Tests/Talewright.Player.E2E.Tests.csproj")
        playwright = [package.attrib["Version"] for package in e2e.findall(".//PackageReference")
                      if package.attrib["Include"] == "Microsoft.Playwright"]
        self.assertEqual(playwright, [metadata["playwright"]])
        global_json = json.loads((self.ROOT / "global.json").read_text())
        self.assertEqual(global_json["sdk"]["version"], metadata["sdk_request"])
        self.assertRegex(metadata["reference"], r"^ghcr\.io/squid-farm/talewright-ui-tests:pw\d+\.\d+\.\d+-r\d+$")
        self.assertEqual(metadata["node_major"], 22)

    def test_ui_workflows_use_prebuilt_image_and_skip_runtime_installs(self):
        for workflow in ["tests.yml", "performance.yml"]:
            with self.subTest(workflow=workflow):
                content = (self.ROOT / ".github/workflows" / workflow).read_text()
                self.assertEqual(content.count(
                    "image: ghcr.io/squid-farm/talewright-ui-tests:pw1.63.0-r1"), 2)
                self.assertNotIn("${{ env.UI_TEST_IMAGE }}", content)
                self.assertIn("python3 .github/docker/ui-tests/verify.py", content)
                self.assertNotIn("player/tests/run_e2e.py install-browsers", content)
                self.assertNotIn("apt-get install -y", content)

    def test_publish_workflow_is_trusted_path_only(self):
        content = (self.ROOT / ".github/workflows/publish-ui-test-image.yml").read_text()
        self.assertIn("workflow_dispatch:", content)
        self.assertIn("branches: [main]", content)
        self.assertNotIn("pull_request_target", content)
        self.assertIn("packages: write", content)
        publish = content.split("\n  publish:\n", 1)[1]
        self.assertIn("if: needs.validate.outputs.publish == 'true' && github.ref == 'refs/heads/main'", publish)
        self.assertNotIn("packages: write", content.split("\n  publish:\n", 1)[0])

    def test_publish_plan_only_publishes_unpublished_tags_from_main(self):
        plan = ui_test_image_plan
        reference = "ghcr.io/squid-farm/talewright-ui-tests:pw1.63.0-r2"
        main = "refs/heads/main"
        cases = [
            ("push", main, True, False, True, True),
            ("push", main, False, True, False, False),
            ("workflow_dispatch", main, True, False, True, True),
            ("workflow_dispatch", main, True, True, False, False),
            ("workflow_dispatch", "refs/heads/topic", True, False, True, False),
            ("pull_request", "refs/pull/1/merge", True, False, True, False),
            ("pull_request", "refs/pull/1/merge", False, True, False, False),
        ]
        for event, ref, changed, exists, build, publish in cases:
            with self.subTest(event=event, ref=ref, changed=changed, exists=exists):
                result = plan.decide(event, ref, changed, exists, reference)
                self.assertEqual((result["build"], result["publish"]), (build, publish))
        for event in ("push", "pull_request"):
            with self.subTest(event=event, stale_tag=True):
                with self.assertRaisesRegex(SystemExit, "Bump the revision"):
                    plan.decide(event, main, True, True, reference)

    def test_publish_plan_detects_image_definition_changes(self):
        plan = ui_test_image_plan
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            git = lambda *args: subprocess.run(["git", *args], cwd=root, check=True,
                                               capture_output=True, text=True).stdout.strip()
            git("init", "-q")
            git("config", "user.email", "ci@example.com")
            git("config", "user.name", "CI")
            context = root / plan.CONTEXT
            context.mkdir(parents=True)
            (context / "Dockerfile").write_text("FROM scratch\n")
            (root / "README.md").write_text("one\n")
            git("add", ".")
            git("commit", "-qm", "base")
            base = git("rev-parse", "HEAD")
            (root / "README.md").write_text("two\n")
            git("commit", "-qam", "docs")
            docs = git("rev-parse", "HEAD")
            (context / "Dockerfile").write_text("FROM scratch\nENV X=1\n")
            git("commit", "-qam", "image")
            image = git("rev-parse", "HEAD")

            self.assertFalse(plan.context_changed(base, docs, root))
            self.assertTrue(plan.context_changed(docs, image, root))
            self.assertTrue(plan.context_changed(plan.ZERO_SHA, image, root))

    def test_container_jobs_preserve_nx_routing_and_selection(self):
        content = (self.ROOT / ".github/workflows/tests.yml").read_text()
        for job, following in (("browser", "linux-desktop"), ("linux-desktop", "editor")):
            with self.subTest(job=job):
                section = content.split(f"\n  {job}:\n", 1)[1].split(f"\n  {following}:\n", 1)[0]
                self.assertIn("needs: plan", section)
                self.assertIn("if: fromJSON(needs.plan.outputs.selected)", section)
                self.assertIn(f"python3 .github/scripts/nx_ci.py run {job}", section)
                self.assertIn("name: nx-ci-plan", section)
                self.assertIn("path: artifacts/ci", section)
                self.assertNotIn("actions/setup-node", section)
                self.assertNotIn("actions/setup-dotnet", section)


if __name__ == "__main__":
    unittest.main()
