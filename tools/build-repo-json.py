#!/usr/bin/env python3
"""Generate (or verify) the Dalamud third-party repository manifest.

A repo.json is a promise to Dalamud about a specific build: the AssemblyVersion in it is the only thing
Dalamud compares against what the user has installed. Maintained by hand it drifts from the csproj, and
the failure is silent in the worst way - the plugin simply never offers an update, or offers one that
installs the same build again. So it is generated from the two files that already hold the truth:

    BmrTranslation/BmrTranslation.csproj   <Version>, the assembly version
    BmrTranslation/BmrTranslation.json     the plugin manifest Dalamud reads from inside the zip

Usage:
    python tools/build-repo-json.py            # write repo.json
    python tools/build-repo-json.py --check    # exit 1 if repo.json is stale (for CI / pre-release)
"""

import argparse
import json
import re
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CSPROJ = ROOT / "BmrTranslation" / "BmrTranslation.csproj"
MANIFEST = ROOT / "BmrTranslation" / "BmrTranslation.json"
REPO_JSON = ROOT / "repo.json"

# Release assets are addressed by the "latest" alias, so the download links never need editing - only the
# version does. DalamudPackager names the archive latest.zip; the release asset keeps that name.
DOWNLOAD = "{repo}/releases/latest/download/latest.zip"

# Copied verbatim from the plugin manifest into the repository entry.
PASSTHROUGH = (
    "Author", "Name", "InternalName", "Description", "Punchline", "ApplicableVersion",
    "RepoUrl", "Tags", "DalamudApiLevel", "LoadPriority", "LoadRequiredState", "LoadSync",
    "CanUnloadAsync", "IsTestingExclusive", "AcceptsFeedback",
)


def assembly_version():
    text = CSPROJ.read_text(encoding="utf-8-sig")
    match = re.search(r"<Version>([^<]+)</Version>", text)
    if not match:
        sys.exit("no <Version> in the csproj - the repository entry cannot state a version")
    version = match.group(1).strip()
    # Dalamud compares four-part versions; a shorter one from the csproj is padded the way MSBuild does
    parts = version.split(".")
    return ".".join(parts + ["0"] * (4 - len(parts))) if len(parts) < 4 else version


def build():
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8-sig"))
    repo_url = manifest["RepoUrl"].rstrip("/")
    download = DOWNLOAD.format(repo=repo_url)

    entry = {key: manifest[key] for key in PASSTHROUGH if key in manifest}
    entry["AssemblyVersion"] = assembly_version()
    entry["TestingAssemblyVersion"] = None
    entry["IsHide"] = False
    entry["DownloadCount"] = 0
    entry["DownloadLinkInstall"] = download
    entry["DownloadLinkUpdate"] = download
    entry["DownloadLinkTesting"] = download
    # Only advertise an icon that exists - a 404 in IconUrl shows up as a broken tile in the installer,
    # which looks worse than the default placeholder
    icon = ROOT / "BmrTranslation" / "images" / "icon.png"
    if icon.exists():
        entry["IconUrl"] = f"{repo_url}/raw/main/BmrTranslation/images/icon.png"
    return [entry]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="verify repo.json is current instead of writing it")
    args = parser.parse_args()

    entries = build()

    if args.check:
        if not REPO_JSON.exists():
            print("repo.json is missing", file=sys.stderr)
            return 1
        current = json.loads(REPO_JSON.read_text(encoding="utf-8-sig"))
        # LastUpdate moves on every write and says nothing about correctness
        stripped = [{k: v for k, v in e.items() if k != "LastUpdate"} for e in current]
        if stripped != entries:
            print("repo.json is stale - run tools/build-repo-json.py", file=sys.stderr)
            return 1
        print(f"repo.json is current ({entries[0]['AssemblyVersion']})")
        return 0

    written = [dict(entry, LastUpdate=int(time.time())) for entry in entries]
    REPO_JSON.write_text(json.dumps(written, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"repo.json written for {entries[0]['InternalName']} {entries[0]['AssemblyVersion']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
