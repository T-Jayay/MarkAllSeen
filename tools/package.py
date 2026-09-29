"""Builds Thunderstore upload zips.

    python tools/package.py

Output: dist/<name>-<version>.zip containing manifest.json, README.md, CHANGELOG.md, icon.png and the plugin DLL,
from thunderstore/<name>/. Before a new release, bump version_number in
the manifest and the version in the plugin source (the script refuses to build if they differ).
"""
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

from PIL import Image

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

PACKAGES = {
    "MarkAllSeen": {
        "project": os.path.join("src", "MarkAllSeen", "MarkAllSeen.csproj"),
        "version_file": os.path.join("src", "MarkAllSeen", "MarkAllSeenPlugin.cs"),
        "version_pattern": r'BepInPlugin\([^,]+,\s*"[^"]+",\s*"([^"]+)"\)',
    },
}


def fail(message):
    print("ERROR:", message)
    sys.exit(1)


def build(name):
    spec = PACKAGES[name]
    ts = os.path.join(ROOT, "thunderstore", name)
    with open(os.path.join(ts, "manifest.json"), encoding="utf-8") as f:
        manifest = json.load(f)

    # Thunderstore's validation rules.
    if manifest["name"] != name:
        fail(f"{name}: manifest name is {manifest['name']}")
    if not re.fullmatch(r"[A-Za-z0-9_]+", manifest["name"]):
        fail(f"{name}: name may only contain letters, digits and underscores")
    if not re.fullmatch(r"\d+\.\d+\.\d+", manifest["version_number"]):
        fail(f"{name}: version_number must be Major.Minor.Patch")
    if len(manifest["description"]) > 250:
        fail(f"{name}: description must be at most 250 characters")
    for required in ("README.md", "icon.png"):
        if not os.path.exists(os.path.join(ts, required)):
            fail(f"{name}: missing {required}")
    if Image.open(os.path.join(ts, "icon.png")).size != (256, 256):
        fail(f"{name}: icon.png must be 256x256")

    source = open(os.path.join(ROOT, spec["version_file"]), encoding="utf-8").read()
    plugin_version = re.search(spec["version_pattern"], source).group(1)
    if plugin_version != manifest["version_number"]:
        fail(f"{name}: plugin version ({plugin_version}) != manifest version_number ({manifest['version_number']})")

    dotnet_root = os.path.join(os.path.expanduser("~"), ".dotnet")
    dotnet = shutil.which("dotnet", path=dotnet_root + os.pathsep + os.environ.get("PATH", ""))
    env = dict(os.environ, DOTNET_ROOT=dotnet_root, DOTNET_CLI_TELEMETRY_OPTOUT="1")
    project = os.path.join(ROOT, spec["project"])
    subprocess.run([dotnet, "build", project, "-c", "Release", "-p:DeployToProfile=false"], check=True, env=env,
                   stdout=subprocess.DEVNULL)
    dll = os.path.join(os.path.dirname(project), "bin", "Release", name + ".dll")

    os.makedirs(os.path.join(ROOT, "dist"), exist_ok=True)
    out = os.path.join(ROOT, "dist", f"{name}-{manifest['version_number']}.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for file in ("manifest.json", "README.md", "CHANGELOG.md", "icon.png"):
            path = os.path.join(ts, file)
            if os.path.exists(path):
                z.write(path, file)
        z.write(dll, name + ".dll")
    print("Wrote", out)


def main():
    names = sys.argv[1:] or list(PACKAGES)
    for name in names:
        if name not in PACKAGES:
            fail(f"unknown package {name}; choose from {', '.join(PACKAGES)}")
        build(name)


if __name__ == "__main__":
    main()
