#!/usr/bin/env python3
"""Verify publication identity and platform on the already-tested VSIX."""

import argparse
import json
from pathlib import Path
import struct
import xml.etree.ElementTree as ET
from zipfile import ZipFile

from package_version import release_identity

TARGETS = (
    "linux-x64", "linux-arm64", "win32-x64", "win32-arm64",
    "darwin-x64", "darwin-arm64",
)


def verify_native(stream, target, name):
    header = stream.read(64)
    arm = target.endswith("-arm64")
    if target.startswith("linux-"):
        valid = (header[:6] == b"\x7fELF\x02\x01"
                 and header[18:20] == struct.pack("<H", 183 if arm else 62))
    elif target.startswith("darwin-"):
        valid = (header[:4] == b"\xcf\xfa\xed\xfe"
                 and header[4:8] == struct.pack("<I", 0x100000c if arm else 0x1000007))
    else:
        valid = len(header) == 64 and header[:2] == b"MZ"
        if valid:
            stream.seek(struct.unpack_from("<I", header, 60)[0])
            valid = stream.read(6) == b"PE\x00\x00" + struct.pack("<H", 0xaa64 if arm else 0x8664)
    if not valid:
        raise ValueError(f"{name} must be a {target} native binary")


def verify_bundle(archive, expected_version=None, target="linux-x64"):
    if target not in TARGETS:
        raise ValueError(f"Unsupported VSIX target: {target}")
    prefix = "extension/server/"
    executable = "Talewright.LanguageServer" + (".exe" if target.startswith("win32-") else "")
    if target.startswith("win32-"):
        native = ["coreclr.dll", "hostfxr.dll", "hostpolicy.dll"]
    else:
        suffix = "dylib" if target.startswith("darwin-") else "so"
        native = [f"lib{name}.{suffix}" for name in ["coreclr", "hostfxr", "hostpolicy"]]
    required = [
        executable, "Talewright.LanguageServer.dll",
        "Talewright.LanguageServer.deps.json", "Talewright.LanguageServer.runtimeconfig.json",
        "Talewright.Authoring.dll", *native,
        "System.Private.CoreLib.dll", "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT",
        "talewright-server.json",
    ]
    missing = {prefix + name for name in required} - set(archive.namelist())
    if missing:
        raise ValueError(f"All-in-one language server assets missing: {sorted(missing)}")
    config = json.loads(archive.read(prefix + "Talewright.LanguageServer.runtimeconfig.json"))["runtimeOptions"]
    if ("framework" in config or "frameworks" in config
            or not any(framework["name"] == "Microsoft.NETCore.App"
                       for framework in config.get("includedFrameworks", []))):
        raise ValueError("Bundled language server requires a separately installed .NET runtime")
    for name in [executable, *native]:
        with archive.open(prefix + name) as stream:
            verify_native(stream, target, name)
    if not target.startswith("win32-") and not (
            archive.getinfo(prefix + executable).external_attr >> 16) & 0o111:
        raise ValueError("Bundled server executable permissions are missing")
    metadata = json.loads(archive.read(prefix + "talewright-server.json"))
    if metadata.get("target") != target or (
            expected_version is not None and metadata.get("version") != expected_version):
        raise ValueError("Bundled server version/target does not match the client release")
    dependencies = json.loads(archive.read(prefix + "Talewright.LanguageServer.deps.json"))
    notices = archive.read("extension/THIRD-PARTY-NOTICES.txt").decode()
    for identity, library in dependencies["libraries"].items():
        if library["type"] == "package" and f"\n{identity}\n" not in notices:
            raise ValueError(f"Bundled dependency license notice missing: {identity}")
    manifest = json.loads(archive.read("extension/package.json"))
    command = manifest["contributes"]["configuration"]["properties"]["talewright.languageServer.command"]
    if command.get("default") != "":
        raise ValueError("Extension must default to the bundled server, not an external executable")


def verify_vsix(path, tag):
    release = release_identity(tag)
    with ZipFile(path) as archive:
        manifest = json.loads(archive.read("extension/package.json"))
        xml = ET.fromstring(archive.read("extension.vsixmanifest"))
        identity = xml.find(".//{*}Identity")
        if identity is None:
            raise ValueError("VSIX has no Identity")
        verify_bundle(archive, release["nuget"], identity.get("TargetPlatform"))
    for key, expected in {
        "name": "talewright", "publisher": "squid-farm", "version": release["vscode"],
    }.items():
        if manifest.get(key) != expected:
            raise ValueError(f"VSIX package.json {key} must be {expected}")
    for key, expected in {
        "Id": "talewright", "Publisher": "squid-farm", "Version": release["vscode"],
    }.items():
        if identity.get(key) != expected:
            raise ValueError(f"VSIX Identity {key} must be {expected}")
    prerelease = any(
        prop.get("Id") == "Microsoft.VisualStudio.Code.PreRelease" and prop.get("Value") == "true"
        for prop in xml.findall(".//{*}Property")
    )
    if prerelease != release["prerelease"]:
        raise ValueError("VSIX pre-release channel does not match the tag")
    return release


def verify_set(directory, tag):
    expected = {
        "talewright.vsix" if target == "linux-x64" else f"talewright-{target}.vsix": target
        for target in TARGETS
    }
    paths = {path.name: path for path in Path(directory).glob("*.vsix")}
    if paths.keys() != expected.keys():
        raise ValueError(f"Release requires exactly these VSIX packages: {sorted(expected)}")
    for name, target in expected.items():
        verify_vsix(paths[name], tag)
        with ZipFile(paths[name]) as archive:
            identity = ET.fromstring(archive.read("extension.vsixmanifest")).find(".//{*}Identity")
            if identity.get("TargetPlatform") != target:
                raise ValueError(f"{name} must target {target}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("vsix")
    parser.add_argument("tag", nargs="?")
    parser.add_argument("--bundle-only", action="store_true")
    parser.add_argument("--all", action="store_true", help="Verify a complete release VSIX directory")
    args = parser.parse_args()
    if args.all and args.tag and not args.bundle_only:
        verify_set(args.vsix, args.tag)
        print("Verified the complete six-platform release VSIX set.")
    elif args.bundle_only and args.tag is None and not args.all:
        with ZipFile(args.vsix) as archive:
            xml = ET.fromstring(archive.read("extension.vsixmanifest"))
            identity = xml.find(".//{*}Identity")
            if identity is None:
                raise ValueError("VSIX has no Identity")
            verify_bundle(archive, target=identity.get("TargetPlatform"))
        print("Verified self-contained platform server assets and bundled launch default.")
    elif args.tag is not None and not args.bundle_only and not args.all:
        verify_vsix(args.vsix, args.tag)
        print("Verified release VSIX identity, version, channel, target and self-contained server.")
    else:
        parser.error("Provide a release tag (with --all for a directory), or --bundle-only for one VSIX")


if __name__ == "__main__":
    main()
