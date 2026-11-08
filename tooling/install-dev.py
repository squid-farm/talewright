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

ROOT = Path(__file__).resolve().parents[1]


def run(command, *, cwd=ROOT, env=None):
    print("+ " + " ".join(str(part) for part in command), flush=True)
    subprocess.run(command, cwd=cwd, env=env, check=True)


def install(args):
    system = {"Linux": ("linux", "linux"), "Darwin": ("osx", "darwin")}
    architectures = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}
    host = system.get(platform.system())
    arch = architectures.get(platform.machine().lower())
    if host is None or arch is None:
        raise RuntimeError("This installer supports Linux and macOS on x64/ARM64.")
    rid = f"{host[0]}-{arch}"
    target = f"{host[1]}-{arch}"
    for command in ("dotnet", "node", "npm", "unzip", args.code):
        if shutil.which(command) is None:
            raise RuntimeError(f"Required command not found: {command}")
    run(["dotnet", "--version"])
    run(["node", "--version"])
    run([args.code, "--version"])

    data_home = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
    home = (data_home / "talewright/dev").resolve()
    bin_dir = args.bin_dir.expanduser().resolve()
    shim = bin_dir / "talewright"
    current = home / "current"
    previous = None
    if shim.exists() and not shim.is_symlink():
        raise RuntimeError(f"Refusing to replace an existing file: {shim}. Choose --bin-dir.")
    if current.is_symlink():
        previous = current.resolve()
        if previous.parent != home or not previous.name.startswith("build-"):
            raise RuntimeError(f"Refusing to replace an unmanaged installation: {current}")
    elif current.exists():
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
    try:
        publish = ["dotnet", "publish", "-c", "Release", "-r", rid,
                   "--self-contained", "true", "-p:PackAsTool=false"]
        run(publish + [str(ROOT / "tooling/cli/src/Talewright.Cli"), "-o", str(build)], env=env)
        run(publish + [str(ROOT / "tooling/debugger/src/Talewright.Debugger"),
                       "-o", str(build / "debug-host")], env=env)
        run([str(build / "Talewright.Cli"), "--help"], env=env)
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

        bin_dir.mkdir(parents=True, exist_ok=True)
        current.unlink(missing_ok=True)
        current.symlink_to(build, target_is_directory=True)
        activated = True
        shim.unlink(missing_ok=True)
        shim.symlink_to(current / "Talewright.Cli")
        run([str(shim), "--help"], env=env)
        if previous and previous.exists():
            shutil.rmtree(previous)
    finally:
        if not activated:
            shutil.rmtree(build)

    print(f"\nInstalled development CLI: {shim}")
    print(f"Installed development VSIX: {vsix}")
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
    parser.add_argument("--bin-dir", type=Path, default=Path.home() / ".local/bin",
                        help="Directory for the talewright symlink (default: ~/.local/bin)")
    args = parser.parse_args()
    try:
        install(args)
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"Development install failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
