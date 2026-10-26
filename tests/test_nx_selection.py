import json
import os
from pathlib import Path
import subprocess
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
NX = ROOT / "nx"
TARGETS = [
    "test",
    "compiler-contract",
    "e2e-web",
    "e2e-linux",
    "e2e-published-web",
    "e2e-published-linux",
    "package-check",
    "check",
    "editor-smoke",
    "editor-full",
    "editor-contract",
    "ci-check",
    "budget",
    "build:release",
]
NO_IMPACT_FILES = (
    ".vscode/tasks.json",
    ".github/skills/backlog-refinement/SKILL.md",
    ".github/agents/reviewer.agent.md",
    ".github/prompts/review.prompt.md",
    ".github/instructions/style.instructions.md",
    ".github/copilot-instructions.md",
    "AGENTS.md",
    "README.md",
    "CONTRIBUTING.md",
    "LICENSE",
    "CODEOWNERS",
    ".github/CODEOWNERS",
    ".gitignore",
    "samples/README.md",
    "examples/CSharpStory/README.md",
)


def affected_graph(*files):
    environment = dict(os.environ, NX_DAEMON="false")
    result = subprocess.run(
        [
            str(NX),
            "affected",
            "-t",
            *TARGETS,
            f"--files={','.join(files)}",
            "--dry-run",
            "--graph=stdout",
        ],
        cwd=ROOT,
        env=environment,
        check=True,
        capture_output=True,
        text=True,
    )
    return json.loads(result.stdout)


class NxSelectionTests(unittest.TestCase):
    def assert_selected(self, graph, *tasks):
        selected = set(graph["tasks"]["tasks"])
        self.assertTrue(set(tasks).issubset(selected), f"Missing {set(tasks) - selected}")
        return selected

    def test_no_impact_changes_select_no_tasks(self):
        for changed in NO_IMPACT_FILES:
            with self.subTest(changed=changed):
                self.assertEqual({}, affected_graph(changed)["tasks"]["tasks"])

    def test_agent_and_vscode_changes_together_select_no_tasks(self):
        graph = affected_graph(
            ".github/skills/backlog-refinement/SKILL.md", ".vscode/tasks.json",
        )
        self.assertEqual({}, graph["tasks"]["tasks"])

    def test_no_impact_changes_do_not_hide_product_changes(self):
        for changed in (
            "engine/src/Talewright.Core/Events/GameEvents.cs",
            "tooling/vscode-talewright/package.json",
            "docs/reference/talemark-syntax.md",
            "src/Talewright.NewModule/Feature.cs",
        ):
            with self.subTest(changed=changed):
                expected = affected_graph(changed)["tasks"]["tasks"]
                self.assertTrue(expected)
                actual = affected_graph(*NO_IMPACT_FILES, changed)["tasks"]["tasks"]
                self.assertEqual(expected, actual)

    def test_highlighting_changes_select_editor_checks_only(self):
        graph = affected_graph("tooling/vscode-talewright/syntaxes/tale.tmLanguage.json")
        selected = self.assert_selected(
            graph,
            "talewright-editor:test",
            "talewright-editor:package-check",
            "talewright-editor:editor-smoke",
        )
        self.assertFalse(any(":e2e-" in task for task in selected))
        self.assertNotIn("Talewright.Core.Tests:test", selected)
        self.assertNotIn("Talewright.Runtime.Tests:test", selected)

    def test_snippet_changes_select_the_compiler_contract(self):
        graph = affected_graph("tooling/vscode-talewright/snippets/tale.json")
        selected = self.assert_selected(
            graph, "talewright-snippets:compiler-contract", "talewright-editor:test",
            "talewright-editor:package-check", "talewright-editor:editor-smoke",
        )
        self.assertNotIn("Talewright.Talemark.Tests:test", selected)
        self.assertNotIn("talewright-workspace:editor-contract", selected)
        self.assertFalse(any(":e2e-" in task for task in selected))
        self.assertNotIn("Talewright.Core.Tests:test", selected)
        self.assertNotIn("Talewright.Runtime.Tests:test", selected)

    def test_rules_changes_select_affected_units_and_downstream_e2e(self):
        graph = affected_graph("engine/src/Talewright.Rules/Talewright.Rules.csproj")
        selected = self.assert_selected(
            graph,
            "Talewright.Rules.Tests:test",
            "Talewright.Talemark.Tests:test",
            "Talewright.Runtime.Tests:test",
            "Talewright.Player.E2E.Tests:e2e-web",
            "Talewright.Player.E2E.Tests:e2e-linux",
        )

    def test_player_ui_changes_select_player_e2e_not_engine_unit_suites(self):
        graph = affected_graph("player/src/Talewright.Player/Components/PlayerToolbar.razor")
        selected = self.assert_selected(graph, "Talewright.Player.E2E.Tests:e2e-web")
        for test in ("Talewright.Core.Tests:test", "Talewright.Rules.Tests:test", "Talewright.Runtime.Tests:test"):
            self.assertNotIn(test, selected)

    def test_runtime_changes_select_persistence_and_published_game_flows(self):
        graph = affected_graph("engine/src/Talewright.Runtime/Talewright.Runtime.csproj")
        selected = self.assert_selected(
            graph,
            "Talewright.Runtime.Tests:test",
            "Talewright.Player.E2E.Tests:e2e-web",
            "Talewright.Player.E2E.Tests:e2e-linux",
            "Talewright.Player.E2E.Tests:e2e-published-web",
            "Talewright.Player.E2E.Tests:e2e-published-linux",
        )

    def test_cli_changes_select_artifact_and_published_game_checks(self):
        graph = affected_graph("tooling/cli/src/Talewright.Cli/Talewright.Cli.csproj")
        self.assert_selected(
            graph,
            "talewright-packaging:package-check",
            "Talewright.Player.E2E.Tests:e2e-published-web",
            "Talewright.Player.E2E.Tests:e2e-published-linux",
        )

    def test_host_and_example_inputs_select_installed_package_verification(self):
        for changed in (
            "apps/web/Program.cs",
            "apps/desktop/Program.cs",
            "examples/CSharpStory/Web/Web.csproj",
            "examples/CSharpStory/Desktop/Desktop.csproj",
            "examples/CSharpStory/Validator/Validator.csproj",
            "examples/CSharpStory/Story/Content/main.tale",
        ):
            with self.subTest(changed=changed):
                graph = affected_graph(changed)
                self.assert_selected(graph, "talewright-packaging:package-check")
                self.assertNotIn("talewright-workspace:package-check", graph["tasks"]["tasks"])

    def test_supporting_character_fragment_lifecycle_selects_consumers_and_publication(self):
        # Nx's changed-file graph receives both paths of a rename and deleted paths, too.
        changes = {
            "addition": ("samples/LostRelic/characters/new.talewright.yaml",),
            "edit": ("samples/LostRelic/characters/guide.talewright.yaml",),
            "rename": ("samples/LostRelic/characters/guide.talewright.yaml",
                       "samples/LostRelic/people/guide.talewright.yaml"),
            "deletion": ("samples/LostRelic/people/deleted.talewright.yaml",),
        }
        for operation, files in changes.items():
            with self.subTest(operation=operation):
                graph = affected_graph(*files)
                selected = self.assert_selected(
                    graph, "Talewright.Talemark.Tests:test",
                    "talewright-editor:compiler-contract",
                    "talewright-packaging:package-check",
                    "Talewright.Player.E2E.Tests:e2e-web",
                    "Talewright.Player.E2E.Tests:e2e-published-web",
                    "Talewright.Player.E2E.Tests:e2e-published-linux",
                )
                self.assertEqual(selected, set(affected_graph(*NO_IMPACT_FILES, *files)["tasks"]["tasks"]))
                self.assertFalse(any(task.startswith("talewright-workspace:") for task in selected))

    def test_configuration_loader_changes_select_archive_and_generated_publication_checks(self):
        for path in (
            "engine/src/Talewright.Talemark/Compilation/StorySource.cs",
            "engine/src/Talewright.Talemark/Parsing/StoryYamlReader.cs",
            "player/src/Talewright.Player/Library/StoryLibraries.cs",
        ):
            with self.subTest(path=path):
                self.assert_selected(
                    affected_graph(path),
                    "Talewright.Player.E2E.Tests:e2e-web",
                    "Talewright.Player.E2E.Tests:e2e-published-web",
                    "Talewright.Player.E2E.Tests:e2e-published-linux",
                    "talewright-packaging:package-check",
                )

    def test_distributed_authoring_inputs_select_editor_and_compiler_verification(self):
        editor = ("talewright-editor:test", "talewright-editor:compiler-contract", "talewright-editor:editor-smoke")
        semantic = ("Talewright.Authoring.Tests:test", "Talewright.LanguageServer.Tests:test")
        changes = {
            "schema": (("tooling/vscode-talewright/schemas/story.schema.json",), editor + semantic),
            "client": (("tooling/vscode-talewright/extension.cjs",), editor),
            "analysis": (("engine/src/Talewright.Talemark/Analysis/StoryAnalysis.cs",),
                         editor + semantic + ("Talewright.Talemark.Tests:test",)),
            "authoring": (("tooling/authoring/src/Talewright.Authoring/StoryNavigation.cs",), editor + semantic),
            "server": (("tooling/language-server/src/Talewright.LanguageServer/LanguageServer.cs",),
                       editor + ("Talewright.LanguageServer.Tests:test",)),
            "fixture addition": (("tooling/vscode-talewright/tests/fixtures/new.talewright.yaml",), editor),
            "fixture rename": (("tooling/vscode-talewright/tests/fixtures/new.talewright.yaml",
                                "tooling/vscode-talewright/tests/fixtures/renamed.talewright.yaml"), editor),
            "fixture deletion": (("tooling/vscode-talewright/tests/fixtures/deleted.talewright.yaml",), editor),
        }
        for name, (files, tasks) in changes.items():
            with self.subTest(change=name):
                selected = self.assert_selected(affected_graph(*files), *tasks)
                self.assertEqual(selected, set(affected_graph(*NO_IMPACT_FILES, *files)["tasks"]["tasks"]))
                self.assertFalse(any(task.startswith("talewright-workspace:") for task in selected))

    def test_inferred_leaf_project_selects_release_compilation(self):
        graph = affected_graph("benchmarks/Talewright.BenchmarkDotNet/Program.cs")
        self.assert_selected(graph, "Talewright.BenchmarkDotNet:build:release")

    def test_shared_and_unclassified_changes_are_conservative(self):
        for changed in (
                "Directory.Build.props", "global.json", "nx.json", ".nxignore",
                "Talewright.slnx", "Directory.Packages.props", "NuGet.Config",
                "src/Talewright.NewModule/Feature.cs"):
            with self.subTest(changed=changed):
                graph = affected_graph(changed)
                selected = self.assert_selected(
                    graph,
                    "talewright-workspace:test",
                    "talewright-workspace:e2e-web",
                    "talewright-workspace:e2e-linux",
                    "talewright-workspace:e2e-published-web",
                    "talewright-workspace:e2e-published-linux",
                    "talewright-workspace:package-check",
                    "talewright-workspace:editor-full",
                    "talewright-workspace:budget",
                    "talewright-workspace:ci-check",
                )

    def test_shared_runner_changes_select_browser_and_native_verification(self):
        for changed in ("player/tests/run_e2e.py", "player/tests/native/atspi_adapter.py"):
            with self.subTest(changed=changed):
                selected = self.assert_selected(
                    affected_graph(changed),
                    "Talewright.Player.E2E.Tests:e2e-web",
                    "Talewright.Player.E2E.Tests:e2e-linux",
                    "Talewright.Player.E2E.Tests:e2e-published-web",
                    "Talewright.Player.E2E.Tests:e2e-published-linux",
                )
                self.assertFalse(any(task.startswith("talewright-workspace:") for task in selected))

    def test_example_settings_preserve_all_example_consumers(self):
        graph = affected_graph("examples/CSharpStory/Directory.Build.props")
        selected = self.assert_selected(
            graph, "talewright-packaging:package-check", "talewright-editor:compiler-contract",
            *(f"{project}:build:release" for project in ("Story", "Web", "Desktop", "Validator")),
        )
        self.assertFalse(any(task.startswith("talewright-workspace:") for task in selected))
        for project in ("Story", "Web", "Desktop", "Validator"):
            edges = graph["graph"]["dependencies"][project]
            self.assertIn("talewright-example-settings", {edge["target"] for edge in edges})

    def test_documentation_changes_select_documentation_checks_only(self):
        graph = affected_graph("docs/developer/architecture.md")
        selected = self.assert_selected(graph, "talewright-docs:check")
        self.assertFalse(any(task.startswith("talewright-editor:") for task in selected))
        self.assertFalse(any(":e2e-" in task for task in selected))
        self.assertNotIn("Talewright.Talemark.Tests:test", selected)

    def test_schema_fixture_inputs_select_editor_contracts(self):
        for changed in (
            "docs/reference/talemark-syntax.md",
            "examples/CSharpStory/Story/Content/story.talewright.yaml",
            "tooling/vscode-talewright/schemas/story.schema.json",
            "engine/tests/Talewright.Talemark.Tests/EditorManifestSchemaTests.cs",
        ):
            with self.subTest(changed=changed):
                self.assert_selected(affected_graph(changed), "talewright-editor:compiler-contract",
                                     "talewright-editor:test")

    def test_task_graph_orders_build_prerequisites(self):
        graph = affected_graph("engine/src/Talewright.Runtime/Talewright.Runtime.csproj")
        dependencies = graph["tasks"]["dependencies"]
        self.assertIn(
            {"id": "Talewright.Runtime.Tests:build:release"},
            dependencies["Talewright.Runtime.Tests:test"],
        )
        self.assertIn(
            {"id": "Talewright.Runtime:build:release"},
            dependencies["Talewright.Runtime.Tests:build:release"],
        )
        self.assertIn(
            {"id": "Talewright.Talemark.Tests:build:release"},
            dependencies["Talewright.Talemark.Tests:test"],
        )
        self.assertIn(
            {"id": "Talewright.Player.E2E.Tests:build:release"},
            dependencies["Talewright.Player.E2E.Tests:e2e-published-web"],
        )

    def test_editor_contracts_share_build_without_concurrent_dotnet_builds(self):
        graph = affected_graph("Directory.Build.props")
        for project in ("talewright-editor", "talewright-snippets"):
            task = f"{project}:compiler-contract"
            self.assertIn(
                {"id": "Talewright.Talemark.Tests:build:release"},
                graph["tasks"]["dependencies"][task],
            )
            command = graph["graph"]["nodes"][project]["data"]["targets"]["compiler-contract"]["options"]["command"]
            self.assertIn("--no-build", command)
            self.assertIn("--no-restore", command)

    def test_inferred_graph_covers_project_references_and_explicit_contracts(self):
        environment = dict(os.environ, NX_DAEMON="false")
        result = subprocess.run(
            [str(NX), "graph", "--print"],
            cwd=ROOT,
            env=environment,
            check=True,
            capture_output=True,
            text=True,
        )
        graph = json.loads(result.stdout)["graph"]
        roots = {
            data["data"]["root"].replace("\\", "/"): name
            for name, data in graph["nodes"].items()
        }
        expected = set()
        projects = 0
        for project_file in ROOT.rglob("*.csproj"):
            if {"bin", "obj", "node_modules"}.intersection(project_file.parts):
                continue
            project_root = project_file.parent.relative_to(ROOT).as_posix()
            source = roots.get(project_root)
            self.assertIsNotNone(source, f"No Nx project for {project_file.relative_to(ROOT)}")
            projects += 1
            for reference in ET.parse(project_file).iter():
                if reference.tag.rsplit("}", 1)[-1] != "ProjectReference":
                    continue
                target_path = (project_file.parent / reference.attrib["Include"].replace("\\", "/")).resolve()
                target = roots.get(target_path.parent.relative_to(ROOT).as_posix())
                self.assertIsNotNone(target, f"No Nx project for reference {target_path}")
                expected.add((source, target))

        for project_file in ROOT.rglob("project.json"):
            project = json.loads(project_file.read_text(encoding="utf-8"))
            expected.update((project["name"], dependency) for dependency in project.get("implicitDependencies", []))

        actual = {
            (source, dependency["target"])
            for source, dependencies in graph["dependencies"].items()
            for dependency in dependencies
        }
        self.assertEqual(30, projects)
        self.assertIn(
            ("Talewright.Authoring", "Talewright.Talemark"), actual,
        )
        self.assertIn(
            ("Talewright.Authoring.Tests", "Talewright.Authoring"), actual,
        )
        self.assertIn(
            ("Talewright.LanguageServer", "Talewright.Authoring"), actual,
        )
        self.assertIn(
            ("Talewright.LanguageServer.Tests", "Talewright.LanguageServer"), actual,
        )
        for runtime in ("Talewright", "Talewright.Player", "Talewright.Cli"):
            self.assertNotIn((runtime, "Talewright.LanguageServer"), actual)
        self.assertNotIn(("Talewright.Player.E2E.Tests", "talewright-ci-tests"), actual)
        self.assertTrue(expected.issubset(actual), f"Missing graph edges: {expected - actual}")
        self.assertNotIn(("talewright-editor", "talewright-docs"), actual)

    def test_browser_matrix_and_uncached_targets_are_preserved(self):
        graph = affected_graph("player/src/Talewright.Player/Components/PlayerToolbar.razor")
        project = graph["graph"]["nodes"]["Talewright.Player.E2E.Tests"]["data"]
        target = project["targets"]["e2e-web"]
        self.assertEqual({"chromium", "firefox", "webkit"}, set(target["configurations"]))
        for name in ("Talewright.Host.Web", "Talewright.Host.Desktop", "Talewright.Player"):
            build = graph["graph"]["nodes"][name]["data"]["targets"]["build:release"]
            self.assertFalse(build["parallelism"], f"Concurrent Razor build enabled for {name}")
        for data in graph["graph"]["nodes"].values():
            for target in data["data"].get("targets", {}).values():
                self.assertFalse(target.get("cache", False), f"Unexpected cache on {data['name']}:{target}")


if __name__ == "__main__":
    unittest.main()
