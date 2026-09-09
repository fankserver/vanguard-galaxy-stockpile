#!/usr/bin/env python3
"""Owner-local packaging; --check validates archive layout without game assets."""
import argparse
import json
from pathlib import Path
import re
import stat
import xml.etree.ElementTree as ET
import zipfile

PLUGIN_ID = "vgstockpile"
METADATA = PLUGIN_ID + ".vgmod.json"
REPOSITORY = "https://github.com/fankserver/vanguard-galaxy-stockpile"
NAMES = {"VGStockpile.dll", "Newtonsoft.Json.dll", METADATA, "README.md", "LICENSE", "THIRD_PARTY_NOTICES.md"}
FIELD_LIMITS = {"pluginId": 128, "author": 256, "description": 4096, "projectUrl": 2048, "updateUrl": 2048, "channel": 32}


def unique_fields(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate metadata field")
        result[key] = value
    return result


def check_metadata(data):
    """Author metadata only: the loader owns identity and installed version."""
    metadata = json.loads(data, object_pairs_hook=unique_fields)
    if set(metadata) - set(FIELD_LIMITS) - {"schemaVersion"}:
        raise ValueError("Unsupported metadata field")
    if metadata.get("schemaVersion") != 1 or metadata.get("pluginId") != PLUGIN_ID:
        raise ValueError("Metadata identity mismatch")
    if metadata.get("channel") != "stable":
        raise ValueError("Unexpected release channel")
    if metadata.get("updateUrl") != REPOSITORY + "/releases/latest/download/update.json":
        raise ValueError("Update feed URL mismatch")
    for field, limit in FIELD_LIMITS.items():
        value = metadata.get(field)
        if field in ("pluginId", "author", "description", "projectUrl", "updateUrl", "channel"):
            if not isinstance(value, str) or not value.strip() or len(value) > limit:
                raise ValueError("Invalid metadata field: " + field)
    if "version" in metadata:
        raise ValueError("Installed version is loader data, not author metadata")
    return metadata


def resource_version(data, key):
    """Read one value from the assembly's own Win32 version resource.

    Type and assembly *reference* strings also contain versions, so scanning raw
    text cannot prove the packaged assembly's identity; these keyed entries can."""
    marker = key.encode("utf-16-le")
    start = data.find(marker)
    if start < 0:
        raise ValueError("Assembly lacks a " + key + " resource entry")
    tail = data[start + len(marker):start + len(marker) + 128]
    values = re.findall(rb"(?:[ -~]\x00){3,}", tail)
    if not values:
        raise ValueError("Unreadable " + key + " resource entry")
    return values[0].decode("utf-16-le")


def check_compiled_version(data, version):
    """A stale or foreign DLL keeps a fresh timestamp, so verify the compiled version."""
    for key in ("Assembly Version", "FileVersion"):
        if resource_version(data, key) != version + ".0":
            raise ValueError("Packaged assembly version differs from the release version")
    if not resource_version(data, "ProductVersion").startswith(version):
        raise ValueError("Packaged assembly product version differs from the release version")


def declared_version(root):
    version = ET.parse(root / "VGStockpile/VGStockpile.csproj").findtext("PropertyGroup/Version")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Invalid release version")
    plugin = (root / "VGStockpile/Plugin.cs").read_text(encoding="utf-8")
    if f'PluginVersion = "{version}"' not in plugin:
        raise ValueError("Plugin and project versions differ")
    return version


def validate(path, version=None):
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        if len(entries) != len(NAMES) or {e.filename for e in entries} != {"VGStockpile/" + n for n in NAMES}:
            raise ValueError("Release file allowlist mismatch")
        if any(stat.S_ISLNK(e.external_attr >> 16) or e.file_size == 0 for e in entries):
            raise ValueError("Empty file or link in release")
        if archive.testzip() is not None:
            raise ValueError("Archive CRC failure")
        check_metadata(archive.read("VGStockpile/" + METADATA))
        if version:
            check_compiled_version(archive.read("VGStockpile/VGStockpile.dll"), version)
    print("PASS: release layout and author metadata, not binary provenance")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", type=Path)
    parser.add_argument("--tag")
    parser.add_argument("--configuration", default="Release")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    version = declared_version(root)
    if args.tag and args.tag != "v" + version:
        raise ValueError("Release tag differs from package version")
    if args.check:
        validate(args.check, version)
    else:
        output = root / "VGStockpile/bin" / args.configuration / "netstandard2.1"
        if {p.name for p in output.glob("*.dll")} != {"VGStockpile.dll", "Newtonsoft.Json.dll"}:
            raise ValueError("Unexpected DLL set; clean and rebuild")
        check_compiled_version((output / "VGStockpile.dll").read_bytes(), version)
        target = root / "dist/VGStockpile.zip"
        target.parent.mkdir(exist_ok=True)
        with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
            for name in sorted(NAMES):
                source = (output if name.endswith(".dll") else root) / name
                if source.is_symlink():
                    raise ValueError("Do not package linked inputs")
                archive.write(source, "VGStockpile/" + name)
        validate(target, version)
        feed = {"schemaVersion": 1, "pluginId": PLUGIN_ID, "channel": "stable",
                "version": version, "releaseUrl": REPOSITORY + "/releases/tag/v" + version}
        (root / "dist/update.json").write_text(json.dumps(feed, indent=2) + "\n", encoding="utf-8")
        print(target)
