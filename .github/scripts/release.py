#!/usr/bin/env python3
"""Version bookkeeping for .github/workflows/release.yml.

prepare:  pick the new plugin version and update every file that carries it
          (Directory.Build.props is the one that sets the DLL's real version -
          missing it once shipped a broken 0.1.1.0, see CLAUDE.md).
manifest: add the new release to manifest.json once the zip exists.

Inputs come from environment variables so workflow inputs never reach a shell.
"""

import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "manifest.json"
PROPS = ROOT / "Directory.Build.props"
CSPROJ = ROOT / "Jellyfin.Plugin.Chromecast" / "Jellyfin.Plugin.Chromecast.csproj"
BUILD_YAML = ROOT / "build.yaml"
README = ROOT / "README.md"
REPO = "DD00031/jellyfin-plugin-chromecast"


def fail(message: str) -> None:
    print(f"::error::{message}")
    sys.exit(1)


def replace_once(path: Path, pattern: str, replacement: str) -> None:
    text = path.read_text(encoding="utf-8")
    new_text, count = re.subn(pattern, replacement, text, count=1, flags=re.MULTILINE)
    if count != 1:
        fail(f"Could not find {pattern!r} in {path.name}")
    path.write_text(new_text, encoding="utf-8")


def output(name: str, value: str) -> None:
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as f:
        f.write(f"{name}<<__EOF__\n{value}\n__EOF__\n")


def prepare() -> None:
    jellyfin = os.environ.get("JELLYFIN_VERSION", "").strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", jellyfin):
        fail(f"Jellyfin version must look like 12.0.1, got {jellyfin!r}")

    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    latest = manifest[0]["versions"][0]["version"]

    version = os.environ.get("PLUGIN_VERSION", "").strip()
    if not version:
        parts = [int(p) for p in latest.split(".")]
        parts[-1] += 1
        version = ".".join(str(p) for p in parts)
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        fail(f"Plugin version must look like 0.1.4.0, got {version!r}")
    if [int(p) for p in version.split(".")] <= [int(p) for p in latest.split(".")]:
        fail(f"Plugin version {version} must be newer than the latest release {latest}")

    target_abi = f"{jellyfin}.0"
    changelog = os.environ.get("CHANGELOG", "").strip() or f"Rebuilt for Jellyfin {jellyfin}."
    changelog = " ".join(changelog.split())

    for tag in ("Version", "AssemblyVersion", "FileVersion"):
        replace_once(PROPS, rf"<{tag}>[^<]*</{tag}>", f"<{tag}>{version}</{tag}>")
    replace_once(
        CSPROJ,
        r"(<JellyfinServerVersion Condition=\"'\$\(JellyfinServerVersion\)' == ''\">)[^<]*(</JellyfinServerVersion>)",
        rf"\g<1>{jellyfin}\g<2>",
    )
    replace_once(BUILD_YAML, r'^version: "[^"]*"', f'version: "{version}"')
    replace_once(BUILD_YAML, r'^targetAbi: "[^"]*"', f'targetAbi: "{target_abi}"')
    replace_once(BUILD_YAML, r"^changelog: >\n", f"changelog: >\n  {version}: {changelog}\n\n")
    replace_once(README, r"early public test release \([0-9.]+\)", f"early public test release ({version})")

    print(f"Plugin {latest} -> {version}, built against Jellyfin {jellyfin} (targetAbi {target_abi})")
    output("version", version)
    output("target_abi", target_abi)
    output("changelog", changelog)


def add_manifest_entry() -> None:
    version = os.environ["VERSION"]
    entry = {
        "version": version,
        "changelog": os.environ["CHANGELOG"],
        "targetAbi": os.environ["TARGET_ABI"],
        "sourceUrl": f"https://github.com/{REPO}/releases/download/v{version}/Jellyfin.Plugin.Chromecast_{version}.zip",
        "checksum": os.environ["CHECKSUM"],
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.0000000Z"),
    }
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    manifest[0]["versions"].insert(0, entry)
    MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(entry, indent=2))


if __name__ == "__main__":
    {"prepare": prepare, "manifest": add_manifest_entry}[sys.argv[1]]()
