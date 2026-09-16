#!/usr/bin/env python3
"""Install or update UnifiedTargetPortal in the Valheim Unified dev profile."""

import base64
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile

R2 = os.path.expanduser("~/Library/Application Support/com.r2modmac")
PROFILE_ID = "b6a8f4c8-6d3b-48c6-95e7-7f5e61634c41"
LOCAL_ID = "local-1789651200000-7d925150bc0e4fc9"
PROJECT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCAL_MOD_DIR = f"{R2}/profiles/{PROFILE_ID}/local_mods/{LOCAL_ID}"
PROFILES_PATH = f"{R2}/profiles.json"


def running(name):
    result = subprocess.run(["pgrep", "-f", name], capture_output=True, text=True)
    return bool(result.stdout.strip())


def fingerprint(path):
    parts = []
    with zipfile.ZipFile(path) as archive:
        for info in archive.infolist():
            if not info.is_dir():
                name = info.filename.replace("\\", "/").lstrip("./")
                parts.append(f"{name}:{info.file_size}:{info.CRC}")
    return hashlib.sha256("\n".join(sorted(parts)).encode()).hexdigest()


def stable_write(path, value):
    with open(path, "w") as handle:
        handle.write(json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n")


def main():
    for app in ("r2modmac.app", "valheim.app"):
        if running(app):
            sys.exit(f"Close {app} first; it may overwrite the profile while this runs.")

    manifest = json.load(open(f"{PROJECT}/manifest.json"))
    version = manifest["version_number"]
    zip_path = f"{PROJECT}/dist/maizz-UnifiedTargetPortal-{version}.zip"
    if not os.path.exists(zip_path):
        sys.exit(f"Package it first: {zip_path} is missing.")

    payload = open(zip_path, "rb").read()
    with zipfile.ZipFile(zip_path) as archive:
        readme = archive.read("README.md").decode("utf-8")
        manifest_sha = hashlib.sha256(archive.read("manifest.json")).hexdigest()
        icon = base64.b64encode(archive.read("icon.png")).decode("ascii")
        dll_path = "plugins/UnifiedTargetPortal/UnifiedTargetPortal.dll"
        dll_sha = hashlib.sha256(archive.read(dll_path)).hexdigest()
        files = [entry for entry in archive.infolist() if not entry.is_dir()]

    metadata = {
        "author": "Local",
        "contentFingerprint": fingerprint(zip_path),
        "description": manifest["description"],
        "displayName": manifest["name"],
        "fileName": os.path.basename(zip_path),
        "fileSize": len(payload),
        "fullName": f"Local-{manifest['name']}-{version}",
        "iconDataUrl": f"data:image/png;base64,{icon}",
        "importedAt": int(time.time() * 1000),
        "manifestSha256": manifest_sha,
        "platforms": ["linux", "mac", "windows"],
        "readme": readme,
        "securityReport": {
            "executableFiles": [dll_path],
            "riskLevel": "medium",
            "totalFiles": len(files),
            "totalUncompressedBytes": sum(item.file_size for item in files),
            "warnings": ["Detected 1 executable or loadable file(s). Custom mods can run code in-game."],
        },
        "sha256": hashlib.sha256(payload).hexdigest(),
        "sourcePath": zip_path,
        "versionNumber": version,
    }

    os.makedirs(LOCAL_MOD_DIR, exist_ok=True)
    shutil.copyfile(zip_path, f"{LOCAL_MOD_DIR}/payload.zip")
    stable_write(f"{LOCAL_MOD_DIR}/metadata.json", metadata)

    profiles = json.load(open(PROFILES_PATH))
    profile = next((item for item in profiles if item.get("id") == PROFILE_ID), None)
    if profile is None:
        sys.exit(f"Profile {PROFILE_ID} not found.")

    existing = next(
        (mod for mod in profile.get("mods", []) if mod.get("localId") == LOCAL_ID),
        None,
    )
    entry = {
        **metadata,
        "enabled": True,
        "iconUrl": metadata["iconDataUrl"],
        "localId": LOCAL_ID,
        "pending_sync": True,
        "source": "local",
        "synced_enabled": True,
        "uuid4": LOCAL_ID,
    }
    entry.pop("iconDataUrl", None)
    if existing is None:
        profile.setdefault("mods", []).append(entry)
    else:
        existing.update(entry)
    profile["needs_sync"] = True

    backup = f"{PROFILES_PATH}.before-unified-target-portal"
    if not os.path.exists(backup):
        shutil.copyfile(PROFILES_PATH, backup)
    stable_write(PROFILES_PATH, profiles)

    print(f"deployed  {version}")
    print(f"  zip     {zip_path}")
    print(f"  sha256  {metadata['sha256']}")
    print(f"  finger  {metadata['contentFingerprint']}")
    print(f"  dll     {dll_sha}")
    print("  sync    pending; press Apply to game in r2modmac")


if __name__ == "__main__":
    main()
