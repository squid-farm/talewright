#!/usr/bin/env python3
"""Build and install the checkout's CLI, debugger and VSIX without publishing."""

import argparse
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
DESKTOP_RUNTIMES = ("linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64")


def run(command, *, cwd=ROOT, env=None):
    print("+ " + " ".join(str(part) for part in command), flush=True)
    subprocess.run(command, cwd=cwd, env=env, check=True)


def publish_template_cache_root(system, data_home):
    if system == "Darwin":
        return Path.home() / "Library/Application Support/Talewright/publish-templates"
    return data_home / "Talewright/publish-templates"


def prepare_publish_templates(build, cache_root, version, env):
    output = build / "template-build"
    assets = build / "template-assets"
    assets.mkdir()
    template_env = dict(env, TALEWRIGHT_PUBLISH_TEMPLATE_OUTPUT=str(output))
    script = ROOT / ".github/scripts/package_publish_template.sh"
    targets = [("web", None), *(("desktop", runtime) for runtime in DESKTOP_RUNTIMES)]
    for target, runtime in targets:
        command = ["bash", str(script), version, target]
        if runtime:
            command.append(runtime)
        run(command, env=template_env)
        name = f"talewright-publish-template-{target}{'-' + runtime if runtime else ''}-{version}.zip"
        for suffix in ("", ".sha256"):
            shutil.move(str(output / (name + suffix)), str(assets / (name + suffix)))
        shutil.rmtree(output / f"build-{target}{'-' + runtime if runtime else ''}")

    output.rmdir()
    destination = cache_root / "development-assets" / version
    destination.parent.mkdir(parents=True, exist_ok=True)
    assets.rename(destination)
    return destination


def install(args):
    system_name = platform.system()
    system = {"Linux": ("linux", "linux"), "Darwin": ("osx", "darwin")}
    architectures = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}
    host = system.get(system_name)
    arch = architectures.get(platform.machine().lower())
    if host is None or arch is None:
        raise RuntimeError("This installer supports Linux and macOS on x64/ARM64.")
    rid = f"{host[0]}-{arch}"
    target = f"{host[1]}-{arch}"
    required = ["dotnet", "node", "npm", "unzip", args.code]
    if args.publish_templates:
        required += ["bash"]
        if shutil.which("sha256sum") is None:
            required += ["shasum"]
    for command in required:
        if shutil.which(command) is None:
            raise RuntimeError(f"Required command not found: {command}")
    run(["dotnet", "--version"])
    run(["node", "--version"])
    run([args.code, "--version"])

    data_home = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
    home = (data_home / "talewright/dev").resolve()
    if args.global_tool:
        cli_home = Path(os.environ.get("DOTNET_CLI_HOME", Path.home()))
        bin_dir = (cli_home / ".dotnet/tools").resolve()
    else:
        bin_dir = args.bin_dir.expanduser().resolve()
    shim = bin_dir / "talewright"
    current = home / "current"
    previous = None
    if not args.global_tool and shim.exists() and not shim.is_symlink():
        raise RuntimeError(f"Refusing to replace an existing file: {shim}. Choose --bin-dir.")
    if not args.global_tool and current.is_symlink():
        previous = current.resolve()
        if previous.parent != home or not previous.name.startswith("build-"):
            raise RuntimeError(f"Refusing to replace an unmanaged installation: {current}")
    elif not args.global_tool and current.exists():
        raise RuntimeError(f"Refusing to replace an existing directory: {current}")

    env = dict(os.environ, MSBUILDDISABLENODEREUSE="1", TALEWRIGHT_VSIX_TARGET=target)
    env.pop("TALEWRIGHT_RELEASE_TAG", None)
    editor = ROOT / "tooling/vscode-talewright"
    if not (editor / "node_modules/.bin/esbuild").exists() or not (
        editor / "node_modules/@vscode/vsce/package.json"
    ).exists():
        run(["npm", "ci"], cwd=editor, env=env)
    home.mkdir(parents=True, exist_ok=True)
    build = Path(tempfile.mkdtemp(prefix="build-", dir=home))
    activated = False
    installation_completed = False
    local_assets = None
    version = f"0.0.0-dev.{uuid.uuid4().hex}" if args.global_tool or args.publish_templates else None
    try:
        if args.global_tool:
            # A fresh identity prevents NuGet from reusing a prior checkout's package.
            packages = build / "packages"
            run(["dotnet", "pack", str(ROOT / "tooling/cli/src/Talewright.Cli"),
                 "-c", "Release", f"-p:Version={version}", "-o", str(packages)], env=env)
            tool_source = ["--version", version, "--source", str(packages), "--no-http-cache"]
            staging = build / "tool"
            run(["dotnet", "tool", "install", "Talewright.Cli", "--tool-path", str(staging)]
                + tool_source, env=env)
            run([str(staging / "talewright"), "--help"], env=env)
        else:
            publish = ["dotnet", "publish", "-c", "Release", "-r", rid,
                       "--self-contained", "true", "-p:PackAsTool=false"]
            if version:
                publish.append(f"-p:Version={version}")
            run(publish + [str(ROOT / "tooling/cli/src/Talewright.Cli"), "-o", str(build)], env=env)
            run(publish + [str(ROOT / "tooling/debugger/src/Talewright.Debugger"),
                           "-o", str(build / "debug-host")], env=env)
            run([str(build / "Talewright.Cli"), "--help"], env=env)
        if args.publish_templates:
            cache_root = publish_template_cache_root(system_name, data_home)
            local_assets = prepare_publish_templates(build, cache_root, version, env)
        run(["npm", "run", "package"], cwd=editor, env=env)
        run(["npm", "run", "test:package"], cwd=editor, env=env)
        name = "talewright.vsix" if target == "linux-x64" else f"talewright-{target}.vsix"
        vsix = editor / "artifacts" / name
        if not vsix.is_file():
            raise RuntimeError(f"Packaging did not produce {vsix}")
        code_args = [args.code, "--install-extension", str(vsix), "--force"]
        if args.profile:
            code_args += ["--profile", args.profile]
        run(code_args, env=env)

        if args.global_tool:
            run(["dotnet", "tool", "update", "Talewright.Cli", "--global", "--allow-downgrade"]
                + tool_source, env=env)
            installation_completed = True
        else:
            bin_dir.mkdir(parents=True, exist_ok=True)
            current.unlink(missing_ok=True)
            current.symlink_to(build, target_is_directory=True)
            activated = True
            installation_completed = True
            shim.unlink(missing_ok=True)
            shim.symlink_to(current / "Talewright.Cli")
        run([str(shim), "--help"], env=env)
        if previous and previous.exists():
            shutil.rmtree(previous)
    finally:
        if local_assets and not installation_completed:
            shutil.rmtree(local_assets)
        if not activated:
            shutil.rmtree(build)

    print(f"\nInstalled development CLI: {shim}")
    print(f"Installed development VSIX: {vsix}")
    if local_assets:
        print(f"Prepared local publish templates: {local_assets}")
    if args.global_tool:
        print("Replaced the global .NET tool; its executable path is unchanged.")
        print("VS Code can discover it automatically when talewright.cli.executablePath is empty.")
    else:
        print('In VS Code, set talewright.cli.executablePath to the CLI path above.')
    print('Clear talewright.languageServer.command to use the newly bundled server.')
    print('Run "Developer: Reload Window" to load the updated extension.')
    if shutil.which("talewright") != str(shim):
        print(f"PATH does not select this CLI. Use its absolute path or prepend {bin_dir} to PATH.")
    print("Rerun this script after fixes. No release or package was published.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--code", default="code", help="VS Code CLI command or path (default: code)")
    parser.add_argument("--profile", help="VS Code profile to install into")
    parser.add_argument("--publish-templates", action="store_true",
                        help="Build and cache all standalone publish templates for this development build")
    destination = parser.add_mutually_exclusive_group()
    destination.add_argument("--bin-dir", type=Path, default=Path.home() / ".local/bin",
                             help="Directory for the talewright symlink (default: ~/.local/bin)")
    destination.add_argument("--global-tool", action="store_true",
                             help="Replace the global .NET tool with a locally packed dev build")
    args = parser.parse_args()
    try:
        install(args)
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"Development install failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
