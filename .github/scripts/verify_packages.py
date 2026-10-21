#!/usr/bin/env python3
"""Check release libraries/tool and optionally run isolated package consumers."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape
from zipfile import ZipFile

from package_version import version_from_tag

PACKAGE_IDS = {"Talewright", "Talewright.Player", "Talewright.Cli"}
ENGINE_ASSEMBLIES = {"Talewright." + module for module in
                     ("Core", "Rules", "Narrative", "Rulesets.Generic", "Tale", "Runtime")}


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
            if identity == "Talewright.Cli":
                required_files = ["README.md", "tools/net10.0/any/DotnetToolSettings.xml",
                                  "tools/net10.0/any/Talewright.Cli.dll",
                                  "tools/net10.0/any/Talewright.Cli.deps.json",
                                  "tools/net10.0/any/Talewright.Cli.runtimeconfig.json"]
            for required in required_files:
                if required not in names:
                    raise ValueError(f"{path}: missing {required}")
            dependencies = {item.attrib["id"]: item.attrib["version"]
                            for item in metadata.findall(".//{*}dependency")}
            if identity == "Talewright.Cli":
                settings = ET.fromstring(package.read("tools/net10.0/any/DotnetToolSettings.xml"))
                command = settings.find("Commands/Command")
                if command is None or command.attrib != {
                        "Name": "talewright", "EntryPoint": "Talewright.Cli.dll", "Runner": "dotnet"}:
                    raise ValueError(f"{path}: invalid talewright tool command")
                for assembly in ENGINE_ASSEMBLIES - {"Talewright.Runtime"}:
                    if f"tools/net10.0/any/{assembly}.dll" not in names:
                        raise ValueError(f"{path}: missing CLI compiler assembly {assembly}")
            elif identity == "Talewright.Player":
                if dependencies.get("Talewright") != version:
                    raise ValueError(f"{path}: Player must depend on engine version {version}")
                for required in ["staticwebassets/talewright.css",
                                 "staticwebassets/Components/StoryPlayer.razor.js",
                                 "buildTransitive/Talewright.Player.props"]:
                    if required not in names:
                        raise ValueError(f"{path}: missing Razor asset {required}")
            else:
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
    print(f"Verified two libraries and CLI tool at {version}", flush=True)


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
    <packageSource key="local"><package pattern="Talewright" /><package pattern="Talewright.Player" /><package pattern="Talewright.Cli" /></packageSource>
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
    public static System.Type Compiler => typeof(Talewright.Tale.Compilation.StoryCompiler);
    public static System.Type Runtime => typeof(Talewright.Runtime.IStorySession);
    public static string NarrativeAssembly => typeof(Talewright.Narrative.Rendering.RenderedDocument).Assembly.GetName().Name;
}
""")
            if identity.endswith(".Player"):
                (project / "PlayerView.razor").write_text("<Talewright.Player.Components.TalewrightApp />\n")
            else:
                (project / "Program.cs").write_text("""using System;
using System.Collections.Generic;
using Talewright.Tale.Compilation;
using Talewright.Runtime;

if (EngineApi.NarrativeAssembly != "Talewright.Narrative")
    throw new Exception("Original Narrative assembly identity was not preserved.");
var source = new StorySource(new Dictionary<string, string>
{
    ["story.yaml"] = "id: smoke\\ntitle: Smoke\\nstart: start\\nruleset: { base: none }\\ncharacters:\\n  player:\\n    first-name: Tester\\n",
    ["main.tale"] = "=== start\\nHello **world**.\\n-> END",
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
                if not {"talewright.css", "Components/StoryPlayer.razor.js"} <= paths:
                    raise ValueError("Player consumer is missing transitive Razor static assets")
        tool_path = root / "tools"
        subprocess.run(["dotnet", "tool", "install", "Talewright.Cli", "--version", version,
                        "--tool-path", str(tool_path), "--configfile", str(config)],
                       cwd=root, env=environment, check=True)
        story = root / "tool-story"
        story.mkdir()
        (story / "story.yaml").write_text(
            "id: tool-smoke\ntitle: Tool smoke\nstart: a\ncharacters:\n  player: {first-name: Tester}\n")
        (story / "main.tale").write_text("=== a\nHello.\n-> END\n")
        result = subprocess.run([str(tool_path / "talewright"), "validate", str(story), "--format", "json"],
                                cwd=root, env=environment, check=True, capture_output=True, text=True)
        if json.loads(result.stdout) != {"schemaVersion": 1, "success": True, "diagnostics": []}:
            raise ValueError("Installed CLI did not validate an isolated story")
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
