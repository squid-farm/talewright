"""Offline public release contracts; never contact a publishing service."""

import io
import json
import os
import re
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



WORKFLOWS = ROOT / ".github/workflows"
KEY = re.compile(r"^([A-Za-z0-9_.-]+):(?:\s+(.*))?$")
TAG_PUSH = {"github.event_name == 'push'", "startsWith(github.ref, 'refs/tags/v')"}


def read_workflow(path):
    """Parse the block-style YAML subset our workflows use; avoids a PyYAML dependency."""
    rows = []
    for raw in path.read_text().splitlines():
        if (text := raw.strip()) and not text.startswith("#"):
            rows.append((len(raw) - len(raw.lstrip(" ")), text))
    workflow, end = _mapping(rows, 0, 0)
    if end != len(rows):
        raise ValueError(f"{path.name}: unsupported YAML near {rows[end][1]!r}")
    return workflow


def _scalar(text):
    if text.startswith("[") and text.endswith("]"):
        return [_scalar(part.strip()) for part in text[1:-1].split(",") if part.strip()]
    if len(text) > 1 and text[0] == text[-1] and text[0] in "'\"":
        return text[1:-1]
    return text


def _mapping(rows, i, indent):
    result = {}
    while i < len(rows) and rows[i][0] == indent and not rows[i][1].startswith("- "):
        match = KEY.match(rows[i][1])
        if not match:
            break
        key, text = match.groups()
        i += 1
        if text in ("|", "|-", ">", ">-"):
            block = []
            while i < len(rows) and rows[i][0] > indent:
                block.append(rows[i][1])
                i += 1
            result[key] = "\n".join(block)
        elif text is not None:
            result[key] = _scalar(text)
        elif i < len(rows) and rows[i][1].startswith("- ") and rows[i][0] >= indent:
            result[key], i = _sequence(rows, i, rows[i][0])
        elif i < len(rows) and rows[i][0] > indent:
            result[key], i = _mapping(rows, i, rows[i][0])
        else:
            result[key] = None
    return result, i


def _sequence(rows, i, indent):
    items = []
    while i < len(rows) and rows[i][0] == indent and rows[i][1].startswith("- "):
        text = rows[i][1][2:].strip()
        if KEY.match(text):
            rows[i] = (indent + 2, text)
            item, i = _mapping(rows, i, indent + 2)
        else:
            item, i = _scalar(text), i + 1
        items.append(item)
    return items, i


def condition_terms(condition):
    return set() if "||" in (condition or "") else {
        term.strip() for term in (condition or "").split("&&")
    }


class PublicationSafeguardTests(unittest.TestCase):
    """Structural checks on the parsed workflows: gating, privileges and credentials."""

    WRITE_PERMISSIONS = {
        ("build.yml", "publish-samples"): {"pages", "id-token"},
        ("build.yml", "publish-nuget"): {"id-token"},
        ("build.yml", "github-release"): {"contents"},
        ("performance.yml", "publish-results"): {"contents"},
        ("publish-nuget.yml", "publish"): {"id-token"},
        ("publish-samples.yml", "deploy"): {"pages", "id-token"},
        ("publish-ui-test-image.yml", "publish"): {"packages"},
    }

    @classmethod
    def setUpClass(cls):
        cls.workflows = {path.name: read_workflow(path) for path in sorted(WORKFLOWS.glob("*.yml"))}
        cls.build = cls.workflows["build.yml"]["jobs"]

    def dependencies(self, jobs, job):
        seen, pending = set(), [job]
        while pending:
            needs = jobs[pending.pop()].get("needs") or []
            for dependency in [needs] if isinstance(needs, str) else needs:
                if dependency not in seen:
                    seen.add(dependency)
                    pending.append(dependency)
        return seen

    def test_every_workflow_parses_into_jobs(self):
        for name, workflow in self.workflows.items():
            with self.subTest(workflow=name):
                self.assertTrue(workflow["jobs"])
                for job, body in workflow["jobs"].items():
                    self.assertTrue("steps" in body or "uses" in body, f"{name}:{job}")

    def test_publication_waits_for_verification_and_only_runs_for_version_tags(self):
        self.assertTrue(
            {"build", "tests", "verification", "cli-archives", "provisional-vsix"}
            <= self.dependencies(self.build, "release-ready")
        )
        for job in ("release-ready", "publish-nuget", "publish-marketplace", "github-release"):
            with self.subTest(job=job):
                self.assertTrue(TAG_PUSH <= condition_terms(self.build[job]["if"]))
                if job != "release-ready":
                    self.assertIn("release-ready", self.dependencies(self.build, job))
        self.assertTrue(
            {"publish-nuget", "publish-marketplace"} <= self.dependencies(self.build, "github-release")
        )
        nuget = self.workflows["publish-nuget.yml"]
        self.assertEqual({"workflow_call"}, set(nuget["on"]))
        self.assertTrue(TAG_PUSH <= condition_terms(nuget["jobs"]["publish"]["if"]))

    def test_pages_deploys_only_a_validated_tag_after_verification_and_smoke(self):
        samples = self.workflows["publish-samples.yml"]
        caller = self.build["publish-samples"]
        self.assertTrue(
            {"build", "tests", "verification"} <= self.dependencies(self.build, "publish-samples")
        )
        self.assertTrue(TAG_PUSH <= condition_terms(caller["if"]))

        selection = next(
            step for step in self.build["select"]["steps"]
            if step.get("name") == "Validate public release tag"
        )
        self.assertEqual({"github.ref_type == 'tag'"}, condition_terms(selection["if"]))
        self.assertIn("package_version.py --tag", selection["run"])
        self.assertIn("--release-json", selection["run"])

        pages = samples["jobs"]
        build = pages["build"]
        self.assertTrue(TAG_PUSH <= condition_terms(build["if"]))
        steps = build["steps"]
        checkout = next(step for step in steps if step.get("uses") == "actions/checkout@v4")
        self.assertEqual("${{ github.sha }}", checkout["with"]["ref"])
        validation = next(step for step in steps if step.get("name") == "Validate the release tag")
        self.assertIn('package_version.py --tag "$GITHUB_REF_NAME" --release-json',
                      validation["run"])
        publish = next(step for step in steps if step.get("name") == "Publish the bundled sample player")
        self.assertIn("publish_samples.py --base-path / --output artifacts/pages", publish["run"])
        smoke = next(step for step in steps if step.get("name") == "Smoke-test the deployment path")
        self.assertEqual("/", smoke["env"]["TALEWRIGHT_WEB_BASE_PATH"])
        self.assertIn("Bundled_sample_assets_work_when_served_at_the_selected_base_path", smoke["run"])
        smoke_index = steps.index(smoke)
        artifact_index = next(
            index for index, step in enumerate(steps)
            if step.get("uses") == "actions/upload-pages-artifact@v3"
        )
        self.assertLess(smoke_index, artifact_index)

        deploy = pages["deploy"]
        self.assertEqual({"build"}, self.dependencies(pages, "deploy"))
        self.assertEqual("github-pages", deploy["environment"]["name"])

    def test_write_permissions_are_limited_to_known_publication_jobs(self):
        actual = {}
        for name, workflow in self.workflows.items():
            self.assertEqual({"contents": "read"}, workflow.get("permissions"), name)
            for job, body in workflow["jobs"].items():
                permissions = body.get("permissions") or {}
                self.assertIsInstance(permissions, dict, f"{name}:{job} must list scopes")
                if writes := {scope for scope, level in permissions.items() if level == "write"}:
                    actual[(name, job)] = writes
        self.assertEqual(self.WRITE_PERMISSIONS, actual)

    def test_publication_secrets_are_explicit_and_bound_to_the_release_environment(self):
        for name, workflow in self.workflows.items():
            for job, body in workflow["jobs"].items():
                with self.subTest(job=f"{name}:{job}"):
                    self.assertNotEqual("inherit", body.get("secrets"))
                    used = set(re.findall(r"secrets\.(\w+)", json.dumps(body))) - {"GITHUB_TOKEN"}
                    if not used:
                        continue
                    if "uses" in body:
                        callee = self.workflows[Path(body["uses"]).name]
                        self.assertEqual(used, set(body["secrets"]))
                        self.assertEqual(used, set(callee["on"]["workflow_call"]["secrets"]))
                        for callee_job in callee["jobs"].values():
                            self.assertEqual("release", callee_job.get("environment"))
                    else:
                        self.assertEqual("release", body.get("environment"))
        self.assertEqual("release", self.build["github-release"].get("environment"))

    def test_nuget_pushes_use_a_short_lived_oidc_key_and_nuget_org_only(self):
        steps = self.workflows["publish-nuget.yml"]["jobs"]["publish"]["steps"]
        logins = [index for index, step in enumerate(steps)
                  if str(step.get("uses", "")).startswith("NuGet/login@")]
        self.assertEqual(1, len(logins))
        login = steps[logins[0]]
        self.assertEqual("${{ secrets.NUGET_USER }}", login["with"]["user"])
        pushes = [index for index, step in enumerate(steps) if "dotnet nuget push" in step.get("run", "")]
        self.assertTrue(pushes)
        for index in pushes:
            step = steps[index]
            self.assertGreater(index, logins[0])
            self.assertIn(f"steps.{login['id']}.outputs.NUGET_API_KEY", json.dumps(step.get("env")))
            self.assertIn("https://api.nuget.org/v3/index.json", step["run"])
            self.assertNotIn("nuget.pkg.github.com", step["run"])

    def test_untrusted_triggers_cannot_reach_publication(self):
        for name, workflow in self.workflows.items():
            with self.subTest(workflow=name):
                self.assertNotIn("pull_request_target", workflow["on"])
        image = self.workflows["publish-ui-test-image.yml"]["jobs"]["publish"]
        self.assertIn("github.ref == 'refs/heads/main'", condition_terms(image["if"]))


if __name__ == "__main__":
    unittest.main()
