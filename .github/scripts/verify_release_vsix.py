#!/usr/bin/env python3
"""Verify publication identity and platform on the already-tested VSIX."""

import argparse
import json
import xml.etree.ElementTree as ET
from zipfile import ZipFile

from package_version import release_identity


def verify_bundle(archive, expected_version=None):
    prefix = "extension/server/"
    required = [
        "Talewright.LanguageServer", "Talewright.LanguageServer.dll",
        "Talewright.LanguageServer.deps.json", "Talewright.LanguageServer.runtimeconfig.json",
        "Talewright.Authoring.dll", "libcoreclr.so", "libhostfxr.so", "libhostpolicy.so",
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
    for name in ["Talewright.LanguageServer", "libcoreclr.so", "libhostfxr.so", "libhostpolicy.so"]:
        with archive.open(prefix + name) as stream:
            header = stream.read(20)
        if header[:6] != b"\x7fELF\x02\x01" or header[18:20] != b"\x3e\x00":
            raise ValueError(f"{name} must be a Linux x64 ELF binary")
    if not (archive.getinfo(prefix + "Talewright.LanguageServer").external_attr >> 16) & 0o111:
        raise ValueError("Bundled server executable permissions are missing")
    metadata = json.loads(archive.read(prefix + "talewright-server.json"))
    if metadata.get("target") != "linux-x64" or (
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
        verify_bundle(archive, release["nuget"])
    identity = xml.find(".//{*}Identity")
    if identity is None:
        raise ValueError("VSIX has no Identity")
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
    if identity.get("TargetPlatform") != "linux-x64":
        raise ValueError("Publication requires the verified all-in-one linux-x64 VSIX; "
                         "a universal/separate-server extension must not be published")
    return release


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("vsix")
    parser.add_argument("tag", nargs="?")
    parser.add_argument("--bundle-only", action="store_true")
    args = parser.parse_args()
    if args.bundle_only and args.tag is None:
        with ZipFile(args.vsix) as archive:
            verify_bundle(archive)
        print("Verified self-contained Linux x64 server assets and bundled launch default.")
    elif args.tag is not None and not args.bundle_only:
        verify_vsix(args.vsix, args.tag)
        print("Verified release VSIX identity, version, channel, target and self-contained server.")
    else:
        parser.error("Provide either a release tag or --bundle-only")


if __name__ == "__main__":
    main()
