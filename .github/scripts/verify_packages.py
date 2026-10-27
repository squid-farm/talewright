#!/usr/bin/env python3
"""Check release libraries/tool and optionally run isolated package consumers."""

import argparse
import json
import os
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape
from zipfile import ZipFile

from package_version import version_from_tag

PACKAGE_IDS = {"Talewright", "Talewright.Player", "Talewright.Cli", "Talewright.LanguageServer"}
ENGINE_ASSEMBLIES = {"Talewright." + module for module in
                     ("Core", "Rules", "Narrative", "Rulesets.Generic", "Talemark", "Runtime")}


def verify_desktop_archive(path):
    with ZipFile(path) as archive:
        names = set(archive.namelist())
        required = {"start.sh", "start.cmd", "start.command", "ENGINE-LICENSE.txt", "DEPLOYMENT.txt"}
        for os_name, native, runtime in [("linux", "Photino.Native.so", "libcoreclr.so"),
                                        ("win", "Photino.Native.dll", "coreclr.dll"),
                                        ("osx", "Photino.Native.dylib", "libcoreclr.dylib")]:
            for cpu in ["x64", "arm64"]:
                rid = f"{os_name}-{cpu}"
                executable = "Talewright.Host.Desktop" + (".exe" if os_name == "win" else "")
                required.update(f"{rid}/{name}" for name in [
                    executable, native, runtime, "Talewright.Host.Desktop.dll",
                    "wwwroot/index.html", "wwwroot/_content/Talewright.Player/talewright.css",
                    "wwwroot/_content/Talewright.Player/Components/StoryPlayer.razor.js",
                    "wwwroot/_content/Talewright.Player/Saves/IndexedDbSaveStore.js"])
                if os_name == "win":
                    required.add(f"{rid}/WebView2Loader.dll")
                config = json.loads(archive.read(f"{rid}/Talewright.Host.Desktop.runtimeconfig.json"))["runtimeOptions"]
                frameworks = config.get("includedFrameworks", [])
                if "framework" in config or "frameworks" in config or not any(
                        framework["name"] == "Microsoft.NETCore.App" for framework in frameworks):
                    raise ValueError(f"{rid}: desktop output requires an installed .NET runtime")
        if not required <= names:
            raise ValueError(f"Universal desktop archive is missing {sorted(required - names)}")
        for name in ["start.sh", "start.command", *(
                f"{os_name}-{cpu}/Talewright.Host.Desktop" for os_name in ["linux", "osx"]
                for cpu in ["x64", "arm64"])]:
            if not (archive.getinfo(name).external_attr >> 16) & 0o111:
                raise ValueError(f"Missing executable permissions: {name}")
    print("Verified six self-contained desktop builds and launchers", flush=True)


def verify(directory, version):
    version_from_tag("v" + version)
    found = {}
    for path in sorted(directory.glob("*.nupkg")):
        with ZipFile(path) as package:
            names = package.namelist()
            specs = [name for name in names if name.endswith(".nuspec")]
            if len(specs) != 1:
                raise ValueError(f"{path}: expected one nuspec")
            metadata = ET.fromstring(package.read(specs[0])).find("{*}metadata")
            if metadata is None:
                raise ValueError(f"{path}: missing package metadata")
            identity = metadata.findtext("{*}id")
            if identity not in PACKAGE_IDS or identity in found:
                raise ValueError(f"{path}: unexpected or duplicate package {identity}")
            if metadata.findtext("{*}version") != version:
                raise ValueError(f"{path}: package version does not match {version}")
            required_files = ["README.md", f"lib/net10.0/{identity}.dll"]
            if identity == "Talewright.LanguageServer":
                required_files = [
                    "README.md", "tools/net10.0/any/DotnetToolSettings.xml",
                    *[f"tools/net10.0/any/Talewright.LanguageServer.{extension}"
                      for extension in ("dll", "deps.json", "runtimeconfig.json")],
                    "tools/net10.0/any/Talewright.Authoring.dll",
                    *[f"tools/net10.0/any/{assembly}.dll" for assembly in ENGINE_ASSEMBLIES - {"Talewright.Runtime"}],
                ]
            if identity == "Talewright.Cli":
                required_files = ["README.md", "tools/net10.0/any/DotnetToolSettings.xml",
                                  "tools/net10.0/any/Talewright.Cli.dll",
                                  "tools/net10.0/any/Talewright.Cli.deps.json",
                                  "tools/net10.0/any/Talewright.Cli.runtimeconfig.json",
                                  "tools/net10.0/any/Talewright.Debugger.dll",
                                  "tools/net10.0/any/debug-assets/index.html",
                                  "tools/net10.0/any/debug-assets/debugger.css",
                                  "tools/net10.0/any/debug-assets/_content/Talewright.Player/talewright.css",
                                  "tools/net10.0/any/publish-assets/web/Talewright.Host.Web.csproj",
                                  "tools/net10.0/any/publish-assets/desktop/Talewright.Host.Desktop.csproj",
                                  "tools/net10.0/any/publish-assets/standalone/web/App.razor",
                                  "tools/net10.0/any/publish-assets/standalone/web/wwwroot/index.html",
                                  "tools/net10.0/any/publish-assets/standalone/desktop/App.razor",
                                  "tools/net10.0/any/publish-assets/player/talewright.css",
                                  "tools/net10.0/any/publish-assets/player/Components/StoryPlayer.razor.js",
                                  "tools/net10.0/any/publish-assets/player/Saves/IndexedDbSaveStore.js",
                                  "tools/net10.0/any/publish-assets/launchers/start.sh",
                                  "tools/net10.0/any/publish-assets/launchers/start.command",
                                  "tools/net10.0/any/publish-assets/launchers/start.cmd",
                                  "tools/net10.0/any/publish-assets/ENGINE-LICENSE.txt"]
            for required in required_files:
                if required not in names:
                    raise ValueError(f"{path}: missing {required}")
            dependencies = {item.attrib["id"]: item.attrib["version"]
                            for item in metadata.findall(".//{*}dependency")}
            if identity == "Talewright.LanguageServer":
                settings = ET.fromstring(package.read("tools/net10.0/any/DotnetToolSettings.xml"))
                command = settings.find("Commands/Command")
                if command is None or command.attrib != {
                        "Name": "talewright-lsp", "EntryPoint": "Talewright.LanguageServer.dll", "Runner": "dotnet"}:
                    raise ValueError(f"{path}: invalid language-server tool command")
                if any("Talewright.Player" in name or "Talewright.Debugger" in name for name in names):
                    raise ValueError(f"{path}: language server includes UI/runtime hosting dependencies")
            if identity in {"Talewright", "Talewright.Player"}:
                tooling = ("talewright.authoring", "talewright.languageserver")
                if any(name.casefold() in tooling for name in dependencies) or any(
                        Path(name).name.casefold().startswith(tuple(name + "." for name in tooling)) for name in names):
                    raise ValueError(f"{path}: runtime package includes authoring infrastructure")
            if identity == "Talewright.Cli":
                settings = ET.fromstring(package.read("tools/net10.0/any/DotnetToolSettings.xml"))
                command = settings.find("Commands/Command")
                if command is None or command.attrib != {
                        "Name": "talewright", "EntryPoint": "Talewright.Cli.dll", "Runner": "dotnet"}:
                    raise ValueError(f"{path}: invalid talewright tool command")
                for assembly in ENGINE_ASSEMBLIES | {"Talewright.Player"}:
                    if f"tools/net10.0/any/{assembly}.dll" not in names:
                        raise ValueError(f"{path}: missing CLI compiler assembly {assembly}")
                for rid in ["linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64"]:
                    extension = "dll" if rid.startswith("win") else "dylib" if rid.startswith("osx") else "so"
                    if f"tools/net10.0/any/runtimes/{rid}/native/Photino.Native.{extension}" not in names:
                        raise ValueError(f"{path}: missing debugger native dependency for {rid}")
            elif identity == "Talewright.Player":
                if any("Debugger" in name or "debug-assets/" in name for name in names):
                    raise ValueError(f"{path}: Player includes author-only tooling")
                if dependencies.get("Talewright") != version:
                    raise ValueError(f"{path}: Player must depend on engine version {version}")
                for required in ["staticwebassets/talewright.css",
                                 "staticwebassets/Components/StoryPlayer.razor.js",
                                 "staticwebassets/Saves/IndexedDbSaveStore.js",
                                 "buildTransitive/Talewright.Player.props"]:
                    if required not in names:
                        raise ValueError(f"{path}: missing Razor asset {required}")
            elif identity == "Talewright":
                if "buildTransitive/Talewright.targets" not in names:
                    raise ValueError(f"{path}: missing story validation build target")
                if any(name.startswith(("Talewright.", "Microsoft.AspNetCore."))
                       for name in dependencies):
                    raise ValueError(f"{path}: headless engine has a UI/component dependency")
                for assembly in ENGINE_ASSEMBLIES:
                    for extension in ("dll", "xml"):
                        required = f"lib/net10.0/{assembly}.{extension}"
                        if required not in names:
                            raise ValueError(f"{path}: missing engine assembly asset {required}")
            found[identity] = path
    if found.keys() != PACKAGE_IDS:
        raise ValueError(f"Expected exactly {sorted(PACKAGE_IDS)}; found {sorted(found)}")
    print(f"Verified two libraries, CLI, and language-server tools at {version}", flush=True)


def smoke(directory, version):
    with tempfile.TemporaryDirectory(prefix="talewright-consumer-") as temporary:
        root = Path(temporary)
        environment = dict(os.environ, NUGET_PACKAGES=str(root / "packages"))
        config = root / "NuGet.Config"
        config.write_text(f"""<configuration>
  <packageSources>
    <clear />
    <add key="local" value="{escape(str(directory), {'"': '&quot;'})}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local"><package pattern="Talewright" /><package pattern="Talewright.Player" /><package pattern="Talewright.Cli" /><package pattern="Talewright.LanguageServer" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
""")
        for name, identity, sdk in [("EngineConsumer", "Talewright", "Microsoft.NET.Sdk"),
                                    ("PlayerConsumer", "Talewright.Player", "Microsoft.NET.Sdk.Razor")]:
            project = root / name
            project.mkdir()
            extra = ('<FrameworkReference Include="Microsoft.AspNetCore.App" />'
                     '<SupportedPlatform Include="browser" />') if identity.endswith(".Player") else ""
            output_type = "<OutputType>Exe</OutputType>" if identity == "Talewright" else ""
            (project / f"{name}.csproj").write_text(f"""<Project Sdk="{sdk}">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors>{output_type}</PropertyGroup>
  <ItemGroup><PackageReference Include="{identity}" Version="{version}" />{extra}</ItemGroup>
</Project>
""")
            (project / "EngineApi.cs").write_text("""public static class EngineApi
{
    public static object CreateRuleset() => new Talewright.Core.Rulesets.RulesetBuilder("consumer").Build();
    public static System.Type Compiler => typeof(Talewright.Talemark.Compilation.StoryCompiler);
    public static System.Type Runtime => typeof(Talewright.Runtime.IStorySession);
    public static string NarrativeAssembly => typeof(Talewright.Narrative.Rendering.RenderedDocument).Assembly.GetName().Name;
}
""")
            if identity.endswith(".Player"):
                (project / "PlayerView.razor").write_text("<Talewright.Player.Components.TalewrightApp />\n")
            else:
                (project / "Program.cs").write_text("""using System;
using System.Collections.Generic;
using Talewright.Talemark.Compilation;
using Talewright.Runtime;

if (args.Length == 1)
{
    var assembly = System.Reflection.Assembly.LoadFile(System.IO.Path.GetFullPath(args[0]));
    using var stream = assembly.GetManifestResourceStream("stories/Published/chapter.fr.tale")
        ?? throw new Exception("Culture-like chapter is missing from the main host assembly.");
    using var reader = new System.IO.StreamReader(stream);
    if (!reader.ReadToEnd().Contains("Culture-like chapter"))
        throw new Exception("Culture-like chapter resource has incorrect content.");
    using var fragment = assembly.GetManifestResourceStream("stories/Published/characters/player.talewright.yaml")
        ?? throw new Exception("Supporting configuration is missing from the main host assembly.");
    using var fragmentReader = new System.IO.StreamReader(fragment);
    if (!fragmentReader.ReadToEnd().Contains("first-name: Tester"))
        throw new Exception("Supporting configuration resource has incorrect content.");
    return;
}
if (EngineApi.NarrativeAssembly != "Talewright.Narrative")
    throw new Exception("Original Narrative assembly identity was not preserved.");
if (EngineApi.Compiler.Assembly.GetName().Name != "Talewright.Talemark")
    throw new Exception("Compiler assembly identity does not match the Talemark namespace.");
var source = new StorySource(new Dictionary<string, string>
{
    ["story.talewright.yaml"] = "game:\\n  id: smoke\\n  title: Smoke\\n  start: start\\n  ruleset: { base: none }\\n",
    ["characters/player.talewright.yaml"] = "characters:\\n  player:\\n    first-name: Tester\\nvars: { greeting: \\"world\\" }\\n",
    ["main.tale"] = "=== start\\nHello **{var:greeting}**.\\n-> END",
});
var result = StoryCompiler.Compile(source);
if (!result.Success)
    throw new Exception(string.Join("\\n", result.Diagnostics));
var frame = new StorySession(result.Story).Start();
if (!frame.IsEnded || !frame.Document.ToPlainText().Contains("Hello world."))
    throw new Exception("Packaged engine did not compile, render, and run the story.");
Console.WriteLine("Original engine assemblies compiled and executed the package story.");
""")
            for command in [["dotnet", "restore", "--configfile", config, "--no-http-cache"],
                            ["dotnet", "build", "-c", "Release", "--no-restore"]]:
                print("+", " ".join(map(str, command)), f"({name})", flush=True)
                subprocess.run(list(map(str, command)), cwd=project, env=environment, check=True)
            assets = json.loads((project / "obj/project.assets.json").read_text())
            libraries = assets["libraries"]
            if f"Talewright/{version}" not in libraries:
                raise ValueError(f"{name}: engine dependency was not restored from the package feed")
            if identity == "Talewright":
                if any(name.startswith("Microsoft.AspNetCore.") for name in libraries):
                    raise ValueError("Engine-only consumer unexpectedly restored Blazor dependencies")
                subprocess.run(["dotnet", "run", "-c", "Release", "--no-build"],
                               cwd=project, env=environment, check=True)
            else:
                manifest = json.loads((project / "obj/Release/net10.0/staticwebassets.build.json").read_text())
                paths = {asset["RelativePath"] for asset in manifest["Assets"]
                         if asset["BasePath"] == "_content/Talewright.Player"}
                if not {"talewright.css", "Components/StoryPlayer.razor.js", "Saves/IndexedDbSaveStore.js"} <= paths:
                    raise ValueError("Player consumer is missing transitive Razor static assets")
        tool_path = root / "tools"
        subprocess.run(["dotnet", "tool", "install", "Talewright.LanguageServer", "--version", version,
                        "--tool-path", str(tool_path), "--configfile", str(config)],
                       cwd=root, env=environment, check=True)
        subprocess.run([str(tool_path / "talewright-lsp"), "--help"],
                       cwd=root, env=environment, check=True)
        subprocess.run([
            "dotnet", "test", str(Path(__file__).resolve().parents[2] /
                                 "tooling/language-server/tests/Talewright.LanguageServer.Tests"),
            "-c", "Release", "--no-build", "--no-restore",
        ], cwd=root, env=dict(environment, TALEWRIGHT_LSP_COMMAND=str(tool_path / "talewright-lsp")), check=True)
        subprocess.run(["dotnet", "tool", "install", "Talewright.Cli", "--version", version,
                        "--tool-path", str(tool_path), "--configfile", str(config)],
                       cwd=root, env=environment, check=True)
        story = root / "tool-story"
        story.mkdir()
        (story / "story.talewright.yaml").write_text(
            "game:\n  id: tool-smoke\n  title: Tool smoke\n  start: a\n")
        (story / "characters").mkdir()
        (story / "characters/player.talewright.yaml").write_text("characters:\n  player: {first-name: Tester}\n")
        (story / "main.tale").write_text("=== a\nHello, {player:first-name}.\n-> translated\n")
        (story / "chapter.fr.tale").write_text("=== translated\nCulture-like chapter.\n-> END\n")
        subprocess.run([str(tool_path / "talewright"), "debug", "--help"],
                       cwd=root, env=environment, check=True, capture_output=True)
        if sys.platform.startswith("linux"):
            headless = dict(environment, DISPLAY="")
            result = subprocess.run([str(tool_path / "talewright"), "debug", str(story)],
                                    cwd=root, env=headless, capture_output=True, text=True)
            if result.returncode != 2 or "TWCLI002" not in result.stderr or "DISPLAY" not in result.stderr:
                raise ValueError("Installed debugger did not reject headless Linux with an actionable error")
        result = subprocess.run([str(tool_path / "talewright"), "validate", str(story), "--format", "json"],
                                cwd=root, env=environment, check=True, capture_output=True, text=True)
        if json.loads(result.stdout) != {"schemaVersion": 1, "success": True, "diagnostics": []}:
            raise ValueError("Installed CLI did not validate an isolated story")
        archive = root / "story.zip"
        subprocess.run([str(tool_path / "talewright"), "publish", str(story), "--output", str(archive)],
                       cwd=root, env=environment, check=True)
        with ZipFile(archive) as published:
            if set(published.namelist()) != {
                    "story.talewright.yaml", "characters/player.talewright.yaml", "main.tale", "chapter.fr.tale"}:
                raise ValueError("Installed CLI did not produce a story-only archive")
        web = root / "web.zip"
        subprocess.run([str(tool_path / "talewright"), "publish", str(story), "-a",
                        "--target", "web", "--output", str(web)], cwd=root, env=environment, check=True)
        with ZipFile(web) as published:
            if any("Debugger" in name or "debug-assets/" in name for name in published.namelist()):
                raise ValueError("Published game includes author-only tooling")
            if "index.html" not in published.namelist() or not any(
                    name.startswith("_framework/") for name in published.namelist()):
                raise ValueError("Installed CLI did not publish a complete standalone web site")
        desktop = root / "desktop.zip"
        subprocess.run([str(tool_path / "talewright"), "publish", str(story), "-a",
                        "--target", "desktop", "--output", str(desktop)], cwd=root, env=environment, check=True)
        verify_desktop_archive(desktop)
        with ZipFile(desktop) as published:
            host = root / "PublishedHost.dll"
            host.write_bytes(published.read("linux-x64/Talewright.Host.Desktop.dll"))
        subprocess.run(["dotnet", "run", "--project", str(root / "EngineConsumer"), "-c", "Release",
                        "--no-build", "--", str(host)], cwd=root, env=environment, check=True)
        example = root / "CSharpStory"
        shutil.copytree(Path(__file__).resolve().parents[2] / "examples/CSharpStory", example,
                        ignore=shutil.ignore_patterns("bin", "obj"))
        for host in ["Web", "Desktop"]:
            subprocess.run(["dotnet", "build", str(example / host), "-c", "Release",
                            f"-p:TalewrightVersion={version}"], cwd=root, env=environment, check=True)
        validation_properties = [
            f"-p:TalewrightStoryProject={example / 'Validator/Validator.csproj'}",
            f"-p:TalewrightStoryRoot={example / 'Story/Content'}",
        ]
        subprocess.run(["dotnet", "build", str(example / "Web"), "-c", "Release",
                        f"-p:TalewrightVersion={version}", *validation_properties],
                       cwd=root, env=environment, check=True, timeout=120)
        shell = example / "Desktop/bin/Release/net10.0/wwwroot/index.html"
        if not shell.is_file() or shell.read_bytes() != (example / "Desktop/wwwroot/index.html").read_bytes():
            raise ValueError("Desktop example did not copy its shell page into the executable output")
        tale = example / "Story/Content/main.tale"
        tale.write_text("=== beginning\n-> missing\n")
        invalid = subprocess.run(["dotnet", "build", str(example / "Web"), "-c", "Release",
                                  f"-p:TalewrightVersion={version}", *validation_properties],
                                 cwd=root, env=environment, capture_output=True, text=True, timeout=120)
        if invalid.returncode == 0 or "main.tale(2," not in invalid.stdout or "error TW2002:" not in invalid.stdout:
            raise ValueError(f"Invalid C# story did not fail with source diagnostics:\n{invalid.stdout}\n{invalid.stderr}")
        print("Both library consumers and installed CLI passed", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("version")
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    directory = args.directory.resolve(strict=True)
    verify(directory, args.version)
    if args.smoke:
        smoke(directory, args.version)


if __name__ == "__main__":
    main()
