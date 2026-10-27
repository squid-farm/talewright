"""Offline public release contracts; never contact a publishing service."""

import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from zipfile import ZipFile, ZipInfo

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".github/scripts"))
from package_version import release_identity
from verify_release_vsix import verify_vsix
import github_release


class PublicVersionTests(unittest.TestCase):
    def test_preview_and_stable_mapping(self):
        for tag, vscode, prerelease in [
            ("v0.1.0-preview.1", "0.1.1", True),
            ("v0.1.0-preview.998", "0.1.998", True),
            ("v0.1.0", "0.1.999", False),
            ("v0.1.1-preview.1", "0.1.1001", True),
            ("v1.2.3-preview.42", "1.2.3042", True),
            ("v1.2.3", "1.2.3999", False),
        ]:
            with self.subTest(tag=tag):
                self.assertEqual(release_identity(tag), {
                    "nuget": tag[1:], "vscode": vscode, "prerelease": prerelease,
                })

    def test_no_collisions_and_stable_follows_all_previews(self):
        versions = [release_identity(f"v0.1.{patch}-preview.{number}")["vscode"]
                    for patch in range(3) for number in range(1, 999)]
        versions += [release_identity(f"v0.1.{patch}")["vscode"] for patch in range(3)]
        self.assertEqual(len(set(versions)), len(versions))
        numeric = lambda value: tuple(map(int, value.split(".")))
        for patch in range(3):
            preview = release_identity(f"v0.1.{patch}-preview.998")["vscode"]
            stable = release_identity(f"v0.1.{patch}")["vscode"]
            following = release_identity(f"v0.1.{patch + 1}-preview.1")["vscode"]
            self.assertLess(numeric(preview), numeric(stable))
            self.assertLess(numeric(stable), numeric(following))

    def test_unsupported_public_tags_fail_closed(self):
        for tag in ["v0.1.0-preview.0", "v0.1.0-preview.999", "v0.1.0-preview.01",
                    "v0.1.0-preview", "v0.1.0-rc.1", "v0.1.0+build", "v01.1.0",
                    "v0.1.0\n", "v2147483648.1.0", "v0.1.2147484"]:
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release_identity(tag)


class ReleaseArtifactTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.vsix = Path(self.temp.name) / "talewright.vsix"
        self.write_vsix()

    def write_vsix(self, *, version="0.1.1", publisher="squid-farm",
                   target="linux-x64", prerelease=True, xml_version=None, bundled=True,
                   dotnet_required=False, executable=True, server_version="0.1.0-preview.1",
                   dependency=None, include_notice=True):
        pre = ('<Property Id="Microsoft.VisualStudio.Code.PreRelease" Value="true" />'
               if prerelease else "")
        with ZipFile(self.vsix, "w") as archive:
            archive.writestr("extension/package.json", json.dumps({
                "name": "talewright", "publisher": publisher, "version": version,
                "contributes": {"configuration": {"properties": {
                    "talewright.languageServer.command": {"default": ""},
                }}},
            }))
            archive.writestr("extension.vsixmanifest", f"""
                <PackageManifest xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
                  <Metadata><Identity Id="talewright" Publisher="{publisher}"
                    Version="{xml_version or version}" TargetPlatform="{target}" />
                    <Properties>{pre}</Properties></Metadata>
                </PackageManifest>""")
            if bundled:
                prefix = "extension/server/"
                for name in ["Talewright.LanguageServer.dll",
                             "Talewright.Authoring.dll", "System.Private.CoreLib.dll",
                             "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"]:
                    archive.writestr(prefix + name, "")
                libraries = {dependency: {"type": "package"}} if dependency else {}
                archive.writestr(prefix + "Talewright.LanguageServer.deps.json",
                                 json.dumps({"libraries": libraries}))
                archive.writestr("extension/THIRD-PARTY-NOTICES.txt",
                                 f"\n{dependency}\n" if dependency and include_notice else "")
                for name in ["Talewright.LanguageServer", "libcoreclr.so", "libhostfxr.so", "libhostpolicy.so"]:
                    info = ZipInfo(prefix + name)
                    info.external_attr = (0o100755 if executable else 0o100644) << 16
                    archive.writestr(info, b"\x7fELF\x02\x01" + b"\x00" * 12 + b"\x3e\x00")
                framework = {"name": "Microsoft.NETCore.App", "version": "10.0.0"}
                options = {"framework": framework} if dotnet_required else {"includedFrameworks": [framework]}
                archive.writestr(prefix + "Talewright.LanguageServer.runtimeconfig.json",
                                 json.dumps({"runtimeOptions": options}))
                archive.writestr(prefix + "talewright-server.json",
                                 json.dumps({"target": "linux-x64", "version": server_version}))

    def test_identity_platform_and_channel(self):
        self.assertTrue(verify_vsix(self.vsix, "v0.1.0-preview.1")["prerelease"])
        self.write_vsix(version="0.1.999", prerelease=False, server_version="0.1.0")
        self.assertFalse(verify_vsix(self.vsix, "v0.1.0")["prerelease"])
        for values in [
            {"target": ""}, {"target": "win32-x64"}, {"version": "0.1.0-preview.1"},
            {"version": "0.1.2"}, {"xml_version": "0.1.2"}, {"publisher": "other"},
            {"prerelease": False},
        ]:
            with self.subTest(values=values):
                self.write_vsix(**values)
                with self.assertRaises(ValueError):
                    verify_vsix(self.vsix, "v0.1.0-preview.1")

    def test_separate_server_or_incomplete_bundle_is_rejected(self):
        for values in [
            {"bundled": False}, {"dotnet_required": True}, {"executable": False},
            {"server_version": "0.1.0-preview.2"},
            {"dependency": "ThirdParty/1.0", "include_notice": False},
        ]:
            with self.subTest(values=values):
                self.write_vsix(**values)
                with self.assertRaises(ValueError):
                    verify_vsix(self.vsix, "v0.1.0-preview.1")

    def release(self, *, draft=False, assets=None):
        return {
            "id": 42, "target_commitish": "abc123", "prerelease": True, "draft": draft,
            "assets": assets or [], "upload_url": "https://uploads.github.com/example{?name,label}",
        }

    def run_publish(self, responses):
        with patch.object(github_release, "request", side_effect=responses) as api:
            github_release.publish(self.vsix, "v0.1.0-preview.1", "abc123", "squid-farm/talewright")
        return api

    def test_first_release_is_draft_until_asset_uploaded(self):
        api = self.run_publish([
            {"sha": "abc123"}, None, {"body": "Generated changes"}, self.release(draft=True),
            {"id": 9}, {},
        ])
        calls = api.call_args_list
        create = calls[3].args[2]
        self.assertTrue(create["draft"])
        self.assertTrue(create["prerelease"])
        self.assertEqual(create["make_latest"], "false")
        self.assertIn("Generated changes", create["body"])
        self.assertIn("save formats", create["body"])
        self.assertIn("Source code (zip)", create["body"])
        self.assertEqual(calls[4].args[2], self.vsix.read_bytes())
        self.assertEqual(calls[5].args[0], "PATCH")
        self.assertFalse(calls[5].args[2]["draft"])

    def test_retry_preserves_identical_asset_and_notes(self):
        asset = {"name": "talewright-linux-x64.vsix", "url": "https://api.github.com/asset"}
        api = self.run_publish([
            {"sha": "abc123"}, self.release(assets=[asset]), self.vsix.read_bytes(),
        ])
        self.assertTrue(all(call.args[0] == "GET" for call in api.call_args_list))

    def test_retry_resumes_draft(self):
        api = self.run_publish([{"sha": "abc123"}, self.release(draft=True), {"id": 9}, {}])
        self.assertEqual(api.call_args_list[-1].args[0], "PATCH")

    def test_publication_failures_are_not_success_shaped(self):
        asset = {"name": "talewright-linux-x64.vsix", "url": "https://api.github.com/asset"}
        for responses in [
            [{"sha": "moved"}],
            [{"sha": "abc123"}, {**self.release(), "prerelease": False}],
            [{"sha": "abc123"}, {**self.release(), "target_commitish": "wrong"}],
            [{"sha": "abc123"}, self.release(assets=[asset]), b"different"],
        ]:
            with self.subTest(responses=responses), self.assertRaises(ValueError):
                self.run_publish(responses)
        failure = HTTPError("https://api.github.com", 401, "Unauthorized", {}, io.BytesIO())
        with self.assertRaises(HTTPError):
            self.run_publish([{"sha": "abc123"}, self.release(draft=True), failure])


class RequestErrorTests(unittest.TestCase):
    def test_only_explicitly_allowed_404_is_absent(self):
        for status in [401, 403, 404, 409, 422, 429, 500]:
            error = HTTPError("https://api.github.com", status, "failure", {}, io.BytesIO())
            with self.subTest(status=status), patch.dict(os.environ, {"GH_TOKEN": "test-token"}):
                with patch.object(github_release, "urlopen", side_effect=error):
                    if status == 404:
                        self.assertIsNone(github_release.request("GET", "https://api.github.com",
                                                                missing_ok=True))
                    else:
                        with self.assertRaises(HTTPError):
                            github_release.request("GET", "https://api.github.com", missing_ok=True)
                    with self.assertRaises(HTTPError):
                        github_release.request("GET", "https://api.github.com")


class PublicationWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.build = (ROOT / ".github/workflows/build.yml").read_text()
        self.nuget = (ROOT / ".github/workflows/publish-nuget.yml").read_text()

    def test_existing_validation_gates_every_publication(self):
        ready = self.build.split("\n  release-ready:\n", 1)[1].split("\n  publish-nuget:", 1)[0]
        self.assertIn("needs: [build, tests, verification]", ready)
        self.assertIn("github.event_name == 'push'", ready)
        self.assertIn("startsWith(github.ref, 'refs/tags/v')", ready)
        self.assertIn("name: nuget-packages", ready)
        self.assertIn("name: talewright-vscode", ready)
        self.assertIn("verify_release_vsix.py", ready)
        self.assertNotIn("exit 1", ready)
        self.assertIn("needs: [build, release-ready]", self.build)
        self.assertIn("needs: release-ready", self.build)
        self.assertIn("needs: [release-ready, publish-nuget, publish-marketplace]", self.build)

    def test_scoped_environment_and_oidc_not_github_packages(self):
        self.assertIn("environment: release", self.nuget)
        self.assertIn("id-token: write", self.nuget)
        self.assertIn("uses: NuGet/login@v1", self.nuget)
        self.assertIn("user: ${{ secrets.NUGET_USER }}", self.nuget)
        self.assertIn("steps.login.outputs.NUGET_API_KEY", self.nuget)
        self.assertIn('source="https://api.nuget.org/v3/index.json"', self.nuget)
        self.assertEqual(self.nuget.count("--skip-duplicate"), 4)
        self.assertNotIn("packages: write", self.nuget + self.build)
        self.assertNotIn("nuget.pkg.github.com", self.nuget)
        for name in ["publish-marketplace", "github-release"]:
            job = self.build.split(f"\n  {name}:\n", 1)[1].split("\n  github-release:", 1)[0]
            self.assertIn("environment: release", job)
        marketplace = self.build.split("\n  publish-marketplace:\n", 1)[1].split("\n  github-release:", 1)[0]
        self.assertIn("secrets.VSCE_PAT", marketplace)
        self.assertIn('--packagePath "$GITHUB_WORKSPACE/artifacts/extension/talewright.vsix"', marketplace)
        self.assertIn("--skip-duplicate", marketplace)
        self.assertIn('if [[ "$PRERELEASE" == true ]]; then args+=(--pre-release); fi', marketplace)
        self.assertNotIn("npm run package", marketplace)
        self.assertNotIn("|| true", self.build + self.nuget)


if __name__ == "__main__":
    unittest.main()
