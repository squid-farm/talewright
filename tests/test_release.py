"""Offline public release contracts; never contact a publishing service."""

import io
import json
import os
import struct
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from zipfile import BadZipFile, ZipFile, ZipInfo

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".github/scripts"))
from package_version import release_identity
from verify_release_vsix import TARGETS, verify_set, verify_vsix
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
                   dependency=None, include_notice=True, binary_target=None, metadata_target=None):
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
                executable_name = "Talewright.LanguageServer"
                if target.startswith("win32-"):
                    executable_name += ".exe"
                    native = ["coreclr.dll", "hostfxr.dll", "hostpolicy.dll"]
                else:
                    suffix = "dylib" if target.startswith("darwin-") else "so"
                    native = [f"lib{name}.{suffix}" for name in ["coreclr", "hostfxr", "hostpolicy"]]
                binary = binary_target or target
                arm = binary.endswith("-arm64")
                if binary.startswith("win32-"):
                    header = (b"MZ" + b"\x00" * 58 + struct.pack("<I", 64)
                              + b"PE\x00\x00" + struct.pack("<H", 0xaa64 if arm else 0x8664))
                elif binary.startswith("darwin-"):
                    header = b"\xcf\xfa\xed\xfe" + struct.pack("<I", 0x100000c if arm else 0x1000007)
                else:
                    header = b"\x7fELF\x02\x01" + b"\x00" * 12 + struct.pack("<H", 183 if arm else 62)
                for name in [executable_name, *native]:
                    info = ZipInfo(prefix + name)
                    info.external_attr = (0o100755 if executable else 0o100644) << 16
                    archive.writestr(info, header)
                framework = {"name": "Microsoft.NETCore.App", "version": "10.0.0"}
                options = {"framework": framework} if dotnet_required else {"includedFrameworks": [framework]}
                archive.writestr(prefix + "Talewright.LanguageServer.runtimeconfig.json",
                                 json.dumps({"runtimeOptions": options}))
                archive.writestr(prefix + "talewright-server.json",
                                 json.dumps({"target": metadata_target or target, "version": server_version}))

    def test_identity_platform_and_channel(self):
        self.assertTrue(verify_vsix(self.vsix, "v0.1.0-preview.1")["prerelease"])
        self.write_vsix(version="0.1.999", prerelease=False, server_version="0.1.0")
        self.assertFalse(verify_vsix(self.vsix, "v0.1.0")["prerelease"])
        for values in [
            {"target": ""}, {"target": "alpine-x64"}, {"version": "0.1.0-preview.1"},
            {"version": "0.1.2"}, {"xml_version": "0.1.2"}, {"publisher": "other"},
            {"prerelease": False},
        ]:
            with self.subTest(values=values):
                self.write_vsix(**values)
                with self.assertRaises(ValueError):
                    verify_vsix(self.vsix, "v0.1.0-preview.1")

    def test_all_platform_bundles_and_mislabeled_binaries(self):
        for target in TARGETS:
            with self.subTest(target=target):
                self.write_vsix(target=target)
                verify_vsix(self.vsix, "v0.1.0-preview.1")
                for values in [
                    {"binary_target": target.rsplit("-", 1)[0] + ("-x64" if target.endswith("-arm64") else "-arm64")},
                    {"binary_target": "linux-arm64" if target == "linux-x64" else "linux-x64"},
                    {"metadata_target": "universal"},
                    {"dotnet_required": True},
                    {"executable": False} if not target.startswith("win32-") else {"bundled": False},
                ]:
                    self.write_vsix(target=target, **values)
                    with self.assertRaises(ValueError):
                        verify_vsix(self.vsix, "v0.1.0-preview.1")

    def test_complete_platform_set_is_required(self):
        directory = self.vsix.parent
        with self.assertRaises(ValueError):
            verify_set(directory, "v0.1.0-preview.1")
        for target in TARGETS:
            self.write_vsix(target=target)
            if target != "linux-x64":
                self.vsix.rename(directory / f"talewright-{target}.vsix")
        self.write_vsix()
        verify_set(directory, "v0.1.0-preview.1")
        wrong = directory / "talewright-darwin-arm64.vsix"
        wrong.write_bytes(self.vsix.read_bytes())
        with self.assertRaises(ValueError):
            verify_set(directory, "v0.1.0-preview.1")
        wrong.unlink()
        self.write_vsix(target="darwin-arm64")
        self.vsix.rename(wrong)
        self.write_vsix()
        (directory / "unexpected.vsix").write_bytes(self.vsix.read_bytes())
        with self.assertRaises(ValueError):
            verify_set(directory, "v0.1.0-preview.1")

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
            "id": 42, "tag_name": "v0.1.0-preview.1", "target_commitish": "main",
            "prerelease": True, "draft": draft,
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
        self.assertIn("provisional", create["body"])
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

    def test_cli_archives_and_checksums_are_uploaded_before_publishing(self):
        archive = self.vsix.parent / "talewright-cli-0.1.0-preview.1-linux-x64.tar.gz"
        checksum = archive.with_name(archive.name + ".sha256")
        archive.write_bytes(b"archive")
        checksum.write_bytes(b"checksum")
        with patch.object(github_release, "request", side_effect=[
            {"sha": "abc123"}, self.release(draft=True), {}, {}, {}, {},
        ]) as api:
            github_release.publish(self.vsix, "v0.1.0-preview.1", "abc123",
                                   "squid-farm/talewright", [archive, checksum])
        self.assertEqual(api.call_args_list[3].args[2], b"archive")
        self.assertEqual(api.call_args_list[4].args[2], b"checksum")
        self.assertEqual(api.call_args_list[-1].args[0], "PATCH")

    def test_provisional_vsix_is_verified_and_uploaded_before_publishing(self):
        extra = self.vsix.parent / "talewright-win32-arm64.vsix"
        self.write_vsix(target="win32-arm64")
        self.vsix.rename(extra)
        self.write_vsix()
        with patch.object(github_release, "request", side_effect=[
            {"sha": "abc123"}, self.release(draft=True), {}, {}, {},
        ]) as api:
            github_release.publish(self.vsix, "v0.1.0-preview.1", "abc123",
                                   "squid-farm/talewright", [extra])
        self.assertIn("talewright-win32-arm64.vsix", api.call_args_list[3].args[1])
        self.assertEqual(api.call_args_list[3].args[2], extra.read_bytes())
        self.assertEqual(api.call_args_list[-1].args[0], "PATCH")
        extra.write_bytes(b"not a VSIX")
        with patch.object(github_release, "request") as api, self.assertRaises(BadZipFile):
            github_release.publish(self.vsix, "v0.1.0-preview.1", "abc123",
                                   "squid-farm/talewright", [extra])
        api.assert_not_called()

    def test_publication_failures_are_not_success_shaped(self):
        asset = {"name": "talewright-linux-x64.vsix", "url": "https://api.github.com/asset"}
        for responses in [
            [{"sha": "moved"}],
            [{"sha": "abc123"}, {**self.release(), "prerelease": False}],
            [{"sha": "abc123"}, {**self.release(), "tag_name": "v0.1.0-preview.2"}],
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

    def test_pages_retains_versions_and_promotes_only_after_candidate_smoke(self):
        samples = (ROOT / ".github/workflows/publish-samples.yml").read_text()
        caller = self.build.split("\n  publish-samples:\n", 1)[1].split("\n  cli-archives:", 1)[0]
        selection = self.build.split("\n  select:\n", 1)[1].split("\n  build:", 1)[0]
        build = samples.split("\n  build:\n", 1)[1].split("\n  deploy:", 1)[0]
        deploy = samples.split("\n  deploy:\n", 1)[1]

        self.assertIn("needs: [build, tests, verification]", caller)
        for section in (caller, build):
            self.assertIn("github.event_name == 'push'", section)
            self.assertIn("github.ref == 'refs/heads/main'", section)
            self.assertIn("startsWith(github.ref, 'refs/tags/v')", section)
            self.assertIn("github.event_name == 'workflow_dispatch'", section)
        self.assertIn('package_version.py --tag "$GITHUB_REF_NAME" --release-json', selection)
        self.assertIn("hosted_site.py plan", build)
        self.assertIn("ref: ${{ github.sha }}", build)
        self.assertIn('publish_samples.py --base-path "$BASE_PATH" --output artifacts/pages', build)
        self.assertIn("TALEWRIGHT_WEB_BASE_PATH: ${{ steps.plan.outputs.base_path }}", build)
        self.assertIn("Bundled_sample_assets_work_when_served_at_the_selected_base_path", build)
        self.assertIn("needs: build", deploy)
        self.assertIn("queue: max", samples)
        self.assertIn("deployments: read", caller)
        self.assertIn("deployments: read", deploy)
        self.assertIn("hosted_store.py restore", deploy)
        self.assertIn("artifact_name: hosted-previous", deploy)
        self.assertLess(deploy.index("Verify the deployed candidate"), deploy.index("Promote the selected release"))
        self.assertLess(deploy.index("Verify the selected root"), deploy.index("Persist the successful snapshot"))
        self.assertIn("Restore the previous site after a failed deployment", deploy)

    def test_existing_validation_gates_every_publication(self):
        ready = self.build.split("\n  release-ready:\n", 1)[1].split("\n  publish-nuget:", 1)[0]
        self.assertIn("needs: [build, tests, verification, cli-archives, provisional-vsix]", ready)
        self.assertIn("github.event_name == 'push'", ready)
        self.assertIn("startsWith(github.ref, 'refs/tags/v')", ready)
        self.assertIn("name: nuget-packages", ready)
        self.assertIn("name: talewright-vscode", ready)
        self.assertIn("verify_release_vsix.py", ready)
        self.assertIn("Verify CLI archive checksums", ready)
        self.assertIn("test \"$(find . -name '*.sha256' | wc -l)\" -eq 6", ready)
        self.assertNotIn("exit 1", ready)
        self.assertIn("needs: [build, release-ready]", self.build)
        self.assertIn("needs: release-ready", self.build)
        self.assertIn("needs: [release-ready, publish-nuget, publish-marketplace]", self.build)

    def test_nuget_caller_explicitly_forwards_required_environment_secret(self):
        caller = self.build.split("\n  publish-nuget:\n", 1)[1].split("\n  publish-marketplace:", 1)[0]
        self.assertIn("uses: ./.github/workflows/publish-nuget.yml", caller)
        self.assertIn("    secrets:\n      NUGET_USER: ${{ secrets.NUGET_USER }}", caller)
        self.assertNotIn("secrets: inherit", caller)
        contract = self.nuget.split("\npermissions:", 1)[0]
        self.assertIn("    secrets:\n      NUGET_USER:\n        required: true", contract)

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
        self.assertIn('for vsix in "$GITHUB_WORKSPACE"/artifacts/extension/*.vsix', marketplace)
        self.assertIn('--packagePath "$vsix"', marketplace)
        self.assertIn('artifacts/extension "$GITHUB_REF_NAME" --all', marketplace)
        self.assertIn("--skip-duplicate", marketplace)
        self.assertIn('if [[ "$PRERELEASE" == true ]]; then args+=(--pre-release); fi', marketplace)
        self.assertNotIn("npm run package", marketplace)
        self.assertNotIn("|| true", self.build + self.nuget)

    def test_provisional_packages_are_inspected_and_attached_without_runtime_claims(self):
        job = self.build.split("\n  provisional-vsix:\n", 1)[1].split("\n  release-ready:", 1)[0]
        self.assertIn("TALEWRIGHT_VSIX_TARGET: ${{ matrix.target }}", job)
        for target in TARGETS:
            if target != "linux-x64":
                self.assertIn(target, job)
        self.assertIn("npm run test:package", job)
        self.assertNotIn("npm run test:editor", job)
        self.assertEqual(self.build.count("pattern: talewright-vscode-*"), 3)
        self.assertIn("--assets artifacts/extension/talewright-*.vsix", self.build)


if __name__ == "__main__":
    unittest.main()
