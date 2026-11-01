"""Retained Pages snapshot and deployment-plan contracts."""

from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.parse import urljoin
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".github/scripts"))
import hosted_site
import hosted_store


class HostedSiteTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.site = self.root / "legacy"
        self.site.mkdir()
        (self.site / "index.html").write_text("The existing release", encoding="utf-8")
        (self.site / "index.html.gz").write_bytes(b"old compressed root")
        (self.site / "root.css").write_text("legacy", encoding="utf-8")
        self.number = 0

    def player(self, base, content="app"):
        self.number += 1
        path = self.root / f"player-{self.number}"
        path.mkdir()
        (path / "index.html").write_text(
            f'<html><head><base href="{base}" /></head><body>'
            '<script src="assets/app.js"></script></body></html>', encoding="utf-8")
        (path / "assets").mkdir()
        (path / "assets/app.js").write_text(content, encoding="utf-8")
        style = path / "_content/Talewright.Player"
        style.mkdir(parents=True)
        (style / "talewright.css").write_text("body {}", encoding="utf-8")
        return path

    def prepare(self, site, plan, player=None, limit=hosted_site.MAX_SITE_BYTES):
        self.number += 1
        candidate = self.root / f"candidate-{self.number}"
        promoted = self.root / f"promoted-{self.number}"
        hosted_site.prepare(site, player, candidate, promoted, plan, limit)
        return candidate, promoted

    def plan(self, tag, commit="a" * 40):
        return hosted_site.deployment_plan("push", "refs/tags/" + tag, commit)

    def test_two_releases_and_dev_retain_versions_root_and_nested_assets(self):
        first = self.plan("v0.4.0-preview.1")
        candidate1, release1 = self.prepare(self.site, first, self.player(first["base_path"], "first"))
        self.assertEqual("The existing release", (candidate1 / "index.html").read_text())
        self.assertFalse((candidate1 / hosted_site.MANIFEST).exists())
        self.assertFalse((release1 / "index.html.gz").exists())
        first_digest = hosted_site.tree_digest(release1 / "versions" / first["label"])

        second = self.plan("v0.5.0-preview.1", "b" * 40)
        candidate2, release2 = self.prepare(release1, second, self.player(second["base_path"], "second"))
        self.assertEqual((release1 / "index.html").read_bytes(), (candidate2 / "index.html").read_bytes())
        self.assertEqual(first_digest, hosted_site.tree_digest(release2 / "versions" / first["label"]))
        manifest = hosted_site.read_manifest(release2)
        self.assertEqual(second["label"], manifest["latest"])
        self.assertEqual([first["label"], second["label"]], [item["tag"] for item in manifest["releases"]])
        self.assertTrue(all(item["prerelease"] for item in manifest["releases"]))

        dev = hosted_site.deployment_plan("push", "refs/heads/main", "c" * 40)
        dev_site, unused = self.prepare(release2, dev, self.player(dev["base_path"], "dev"))
        self.assertFalse(unused.exists())
        self.assertEqual((release2 / "index.html").read_bytes(), (dev_site / "index.html").read_bytes())
        self.assertEqual(second["label"], hosted_site.read_manifest(dev_site)["latest"])
        self.assertEqual(first_digest, hosted_site.tree_digest(dev_site / "versions" / first["label"]))

        handler = partial(SimpleHTTPRequestHandler, directory=str(dev_site))
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(thread.join)
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        origin = f"http://127.0.0.1:{server.server_port}"
        for base, expected in ((first["base_path"], "first"), (second["base_path"], "second"),
                               (dev["base_path"], "dev")):
            with self.subTest(base=base):
                with urlopen(origin + base) as response:
                    index = response.read().decode()
                    self.assertIn(f'<base href="{base}" />', index)
                    self.assertEqual(base == dev["base_path"],
                                     "Experimental development build</div>" in index)
                with urlopen(urljoin(origin + base, "assets/app.js")) as response:
                    self.assertEqual(expected, response.read().decode())
        hosted_site.smoke(origin, dev_site, second, root=True, attempts=1)
        hosted_site.smoke(origin, dev_site, dev, attempts=1)
        with self.assertRaisesRegex(RuntimeError, "root does not point"):
            hosted_site.smoke(origin, dev_site, first, root=True, attempts=1)

    def test_failed_release_smoke_keeps_the_previous_root_and_version_usable(self):
        first = self.plan("v0.4.0-preview.1")
        _, release = self.prepare(self.site, first, self.player(first["base_path"], "first"))
        second = self.plan("v0.5.0-preview.1", "b" * 40)
        candidate, _ = self.prepare(release, second, self.player(second["base_path"], "second"))
        (candidate / "versions" / second["label"] / "_content/Talewright.Player/talewright.css").unlink()
        handler = partial(SimpleHTTPRequestHandler, directory=str(candidate))
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(thread.join)
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        origin = f"http://127.0.0.1:{server.server_port}"
        with self.assertRaisesRegex(RuntimeError, "smoke test failed"):
            hosted_site.smoke(origin, candidate, second, attempts=1)
        with urlopen(origin + "/index.html") as response:
            self.assertIn(first["base_path"], response.read().decode())
        with urlopen(origin + first["base_path"] + "assets/app.js") as response:
            self.assertEqual("first", response.read().decode())

    def test_existing_version_is_immutable_and_identical_retry_is_allowed(self):
        plan = self.plan("v0.4.0-preview.1")
        player = self.player(plan["base_path"])
        _, release = self.prepare(self.site, plan, player)
        _, retry = self.prepare(release, plan, player)
        self.assertEqual(hosted_site.tree_digest(release / "versions" / plan["label"]),
                         hosted_site.tree_digest(retry / "versions" / plan["label"]))
        before = hosted_site.tree_digest(release)
        changed = self.player(plan["base_path"], "different")
        with self.assertRaisesRegex(ValueError, "Refusing to overwrite retained version"):
            self.prepare(release, plan, changed)
        self.assertEqual(before, hosted_site.tree_digest(release))

    def test_rollback_selects_a_retained_release_without_removing_anything(self):
        first = self.plan("v0.4.0-preview.1")
        _, release1 = self.prepare(self.site, first, self.player(first["base_path"]))
        second = self.plan("v0.5.0-preview.1", "b" * 40)
        _, release2 = self.prepare(release1, second, self.player(second["base_path"]))
        rollback = hosted_site.deployment_plan("workflow_dispatch", "refs/heads/main", "c" * 40,
                                               first["label"])
        _, reverted = self.prepare(release2, rollback)
        self.assertEqual(first["label"], hosted_site.read_manifest(reverted)["latest"])
        self.assertIn(first["base_path"], (reverted / "index.html").read_text())
        self.assertTrue((reverted / "versions" / second["label"] / "index.html").is_file())
        rollback = hosted_site.deployment_plan("workflow_dispatch", "refs/heads/main", "c" * 40, "v0.6.0")
        with self.assertRaisesRegex(ValueError, "unretained release"):
            self.prepare(release2, rollback)

    def test_invalid_or_oversized_builds_leave_the_previous_site_unchanged(self):
        before = hosted_site.tree_digest(self.site)
        plan = self.plan("v0.4.0-preview.1")
        with self.assertRaisesRegex(ValueError, "base path"):
            self.prepare(self.site, plan, self.player("/wrong/"))
        with self.assertRaisesRegex(ValueError, "Pages limit"):
            self.prepare(self.site, plan, self.player(plan["base_path"]), limit=100)
        with patch.object(hosted_site, "MAX_FILES", 1):
            with self.assertRaisesRegex(ValueError, "file limit"):
                self.prepare(self.site, plan, self.player(plan["base_path"]))
        self.assertEqual(before, hosted_site.tree_digest(self.site))

    def test_corrupt_or_untracked_retained_content_fails_closed(self):
        plan = self.plan("v0.4.0-preview.1")
        _, release = self.prepare(self.site, plan, self.player(plan["base_path"]))
        (release / "versions" / plan["label"] / "assets/app.js").write_text("tampered")
        with self.assertRaisesRegex(ValueError, "has changed"):
            hosted_site.read_manifest(release)
        (self.site / "versions").mkdir()
        with self.assertRaisesRegex(ValueError, "reserved"):
            hosted_site.read_manifest(self.site)

    def test_plan_accepts_only_main_pushes_valid_tags_and_explicit_main_rollbacks(self):
        commit = "a" * 40
        self.assertEqual("dev", hosted_site.deployment_plan("push", "refs/heads/main", commit)["kind"])
        self.assertEqual("release", self.plan("v0.4.0-preview.1")["kind"])
        for event, ref, rollback in [
            ("push", "refs/heads/feature", ""),
            ("pull_request", "refs/tags/v0.4.0", ""),
            ("workflow_dispatch", "refs/heads/main", ""),
            ("workflow_dispatch", "refs/heads/feature", "v0.4.0"),
            ("push", "refs/tags/v0.4.0-rc.1", ""),
            ("push", "refs/tags/v0.4.0/../dev", ""),
        ]:
            with self.subTest(event=event, ref=ref, rollback=rollback), self.assertRaises(ValueError):
                hosted_site.deployment_plan(event, ref, commit, rollback)
        with self.assertRaisesRegex(ValueError, "commit SHA"):
            hosted_site.deployment_plan("push", "refs/heads/main", "not-a-sha")
        corrupted = self.plan("v0.4.0")
        corrupted["base_path"] = "/dev/"
        with self.assertRaisesRegex(ValueError, "does not match"):
            self.prepare(self.site, corrupted)


class HostedDeploymentTests(unittest.TestCase):
    repository = "owner/repo"

    def status(self, run, state):
        return {
            "state": state,
            "log_url": f"https://github.com/{self.repository}/actions/runs/{run}/job/1",
        }

    def test_previous_deployment_skips_the_current_run(self):
        deployments = [{"id": 13}, {"id": 12}]
        statuses = {13: self.status(100, "in_progress"), 12: self.status(99, "success")}
        with patch.object(hosted_store, "deployments", return_value=deployments), \
                patch.object(hosted_store, "deployment_status",
                             side_effect=lambda repository, deployment: statuses[deployment["id"]]):
            self.assertEqual({"id": 12, "run": 99},
                             hosted_store.previous_deployment(self.repository, 100))

    def test_concurrent_or_unidentifiable_deployment_fails_closed(self):
        with patch.object(hosted_store, "deployments", return_value=[{"id": 12}]), \
                patch.object(hosted_store, "deployment_status", return_value=self.status(99, "in_progress")):
            with self.assertRaisesRegex(RuntimeError, "in progress"):
                hosted_store.previous_deployment(self.repository, 100)
        with patch.object(hosted_store, "deployments", return_value=[{"id": 12}]), \
                patch.object(hosted_store, "deployment_status", return_value={"state": "success"}):
            with self.assertRaisesRegex(RuntimeError, "no associated Actions run"):
                hosted_store.previous_deployment(self.repository, 100)

    def test_current_run_requires_a_unique_environment_deployment(self):
        deployments = [{"id": 13}, {"id": 12}]
        with patch.object(hosted_store, "deployments", return_value=deployments), \
                patch.object(hosted_store, "deployment_status", return_value=self.status(100, "in_progress")):
            with self.assertRaisesRegex(RuntimeError, "Multiple Pages deployments"):
                hosted_store.current_deployment_id(self.repository, 100)


class HostedStoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repository = self.root / "repository"
        self.remote = self.root / "remote.git"
        self.git("init", "--bare", "-q", str(self.remote))
        self.git("init", "-q", str(self.repository))
        self.git("-C", str(self.repository), "config", "user.name", "Test")
        self.git("-C", str(self.repository), "config", "user.email", "test@example.com")
        self.git("-C", str(self.repository), "commit", "--allow-empty", "-qm", "initial")
        self.git("-C", str(self.repository), "remote", "add", "origin", str(self.remote))
        self.site = self.root / "site"
        self.site.mkdir()
        (self.site / "index.html").write_text("existing release", encoding="utf-8")
        self.info = self.root / "restore.json"
        self.info.write_text(json.dumps({"schema": 1, "base_oid": None, "previous_deployment_id": 10}))

    def git(self, *arguments):
        return subprocess.run(["git", *arguments], check=True, capture_output=True, text=True).stdout.strip()

    def test_persists_and_restores_the_deployed_snapshot_with_compare_and_swap(self):
        hosted_store.persist(self.repository, self.site, self.info, 11, 42)
        oid = hosted_store.remote_state_oid(self.repository)
        self.assertRegex(oid, r"^[0-9a-f]{40}$")
        restored, information = self.root / "restored", self.root / "restored.json"
        with patch.object(hosted_store, "previous_deployment", return_value={"id": 11, "run": 100}):
            hosted_store.restore(self.repository, "owner/repo", 101, restored, information)
        self.assertEqual("existing release", (restored / "index.html").read_text())
        self.assertEqual(oid, json.loads(information.read_text())["base_oid"])

        (restored / "new.css").write_text("new", encoding="utf-8")
        hosted_store.persist(self.repository, restored, information, 12, 43)
        with self.assertRaisesRegex(RuntimeError, "changed during deployment"):
            hosted_store.persist(self.repository, self.site, information, 13, 44)
        self.assertNotEqual(oid, hosted_store.remote_state_oid(self.repository))

    def test_bootstrap_refreshes_only_an_unmanaged_legacy_snapshot(self):
        hosted_store.persist(self.repository, self.site, self.info, 11, 42)
        refreshed = self.root / "refreshed"
        info = self.root / "refreshed.json"
        newer = self.root / "newer"
        shutil.copytree(self.site, newer)
        (newer / "index.html").write_text("new legacy release", encoding="utf-8")
        with patch.object(hosted_store, "previous_deployment", return_value={"id": 12, "run": 100}), \
                patch.object(hosted_store, "download_live_snapshot",
                             side_effect=lambda repository, deployment, destination: shutil.copytree(newer, destination)):
            hosted_store.restore(self.repository, "owner/repo", 101, refreshed, info)
        self.assertEqual("new legacy release", (refreshed / "index.html").read_text())

        plan = hosted_site.deployment_plan("push", "refs/heads/main", "a" * 40)
        player = self.root / "player"
        player.mkdir()
        (player / "index.html").write_text('<base href="/dev/" /><body></body>', encoding="utf-8")
        managed, promoted = self.root / "managed", self.root / "unused"
        hosted_site.prepare(refreshed, player, managed, promoted, plan)
        hosted_store.persist(self.repository, managed, info, 12, 43)
        with patch.object(hosted_store, "previous_deployment", return_value={"id": 13, "run": 102}):
            with self.assertRaisesRegex(RuntimeError, "changed outside"):
                hosted_store.restore(self.repository, "owner/repo", 103,
                                     self.root / "conflict", self.root / "conflict.json")

    def test_snapshot_extraction_rejects_traversal_links_and_duplicate_files(self):
        for name, kind in [("../escape", "file"), ("linked", "symlink"), ("same", "duplicate")]:
            with self.subTest(name=name, kind=kind):
                archive = self.root / f"{kind}.tar"
                with tarfile.open(archive, "w") as tar:
                    entry = tarfile.TarInfo(name)
                    if kind == "symlink":
                        entry.type = tarfile.SYMTYPE
                        entry.linkname = "../escape"
                        tar.addfile(entry)
                    else:
                        entry.size = 1
                        tar.addfile(entry, io.BytesIO(b"x"))
                        if kind == "duplicate":
                            tar.addfile(entry, io.BytesIO(b"x"))
                with self.assertRaises(ValueError):
                    hosted_store.extract_tar(archive, self.root / f"extract-{kind}")
        self.assertFalse((self.root / "escape").exists())


if __name__ == "__main__":
    unittest.main()
