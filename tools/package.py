"""Build the Thunderstore upload zip of each package in this repo.

Usage (needs Python 3.8+ and the .NET SDK 8 or later):

    python tools/package.py [--force] [package ...]

Without package names it packages everything in PACKAGES. For each
package it:

1. checks thunderstore/<name>/ against Thunderstore's rules and this
   repo's conventions: manifest.json, README.md, a CHANGELOG.md entry
   "## <version>", a 256x256 PNG icon.png, and PluginVersion in
   src/<name>/<name>Plugin.cs equal to the manifest's version_number;
2. refuses to package uncommitted changes to the build inputs, or to
   overwrite an existing dist/<name>-<version>.zip, because an uploaded
   version can never be changed (--force skips both checks, for test
   builds);
3. builds src/<name>/<name>.csproj in Release without deploying it;
4. writes dist/<name>-<version>.zip with manifest.json, README.md,
   CHANGELOG.md, icon.png and the plugin DLL at the root. The zip is
   reproducible for a given Python build (the compressed bytes depend
   on its zlib): fixed timestamps and permissions, text files with LF
   line endings as committed, and never a PDB.

Projects that aren't in PACKAGES, such as test-only plugins, are never
packaged.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import zipfile
from pathlib import Path
from typing import NoReturn

# The packages this repo publishes. Each has its store files in
# thunderstore/<name>/ and its plugin in src/<name>/<name>.csproj.
PACKAGES = ("MarkAllSeen",)

ROOT = Path(__file__).resolve().parent.parent
TEAM = "revoreverse"
WEBSITE_URL = "https://github.com/T-Jayay/{name}"
# Files and folders the build reads; a release zip must match a commit.
BUILD_INPUTS = ("src", "thunderstore", "Directory.Build.props",
                "Directory.Build.targets")
PACKAGE_FILES = ("manifest.json", "README.md", "CHANGELOG.md", "icon.png")
TEXT_FILES = ("manifest.json", "README.md", "CHANGELOG.md")

# Thunderstore's rules for manifest.json and icon.png.
NAME_PATTERN = re.compile(r"[A-Za-z0-9_]{1,128}")
VERSION_PATTERN = re.compile(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)")
DEPENDENCY_PATTERN = re.compile(r"([A-Za-z0-9_]+-[A-Za-z0-9_]+)-\d+\.\d+\.\d+")
MAX_DESCRIPTION_LENGTH = 250
ICON_SIZE = (256, 256)

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
UTF8_BOM = b"\xef\xbb\xbf"
MIN_SDK_MAJOR = 8
# Every zip entry gets the same timestamp (the earliest a zip can store)
# and permissions, so the same files give the same zip (with the same
# Python build, see the docstring).
ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)
ZIP_UNIX_SYSTEM = 3
ZIP_FILE_MODE = 0o100644  # regular file, rw-r--r--


class PackageError(Exception):
    """A problem that stops packaging, reported without a traceback."""


def fail(message: str) -> NoReturn:
    """Stop packaging with an error message."""
    raise PackageError(message)


def relative(path: Path) -> str:
    """Return a path relative to the repo root, for messages."""
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        return str(path)


def read_bytes(path: Path) -> bytes:
    """Read a file, failing with a clear message if it can't be read."""
    try:
        return path.read_bytes()
    except FileNotFoundError:
        fail(f"{relative(path)} is missing")
    except OSError as error:
        fail(f"can't read {relative(path)}: {error.strerror}")


def read_text(path: Path) -> str:
    """Read a UTF-8 text file (without a byte order mark)."""
    data = read_bytes(path)
    if data.startswith(UTF8_BOM):
        fail(f"{relative(path)} starts with a byte order mark; "
             "save it as UTF-8 without BOM")
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError as error:
        fail(f"{relative(path)} isn't valid UTF-8 "
             f"(byte {error.start}: {error.reason})")


def run(command: list[str],
        env: dict[str, str] | None = None) -> subprocess.CompletedProcess:
    """Run a command in the repo root and capture its output."""
    try:
        return subprocess.run(command, cwd=ROOT, env=env,
                              capture_output=True, encoding="utf-8",
                              errors="replace", check=False)
    except OSError as error:
        fail(f"can't run {command[0]}: {error.strerror}")


def check_committed() -> None:
    """Fail if the build inputs have changes that aren't committed."""
    try:
        result = run(["git", "status", "--porcelain",
                      "--untracked-files=all", "--", *BUILD_INPUTS])
    except PackageError:
        fail("git isn't available, so uncommitted changes can't be "
             "checked (--force skips this check)")
    if result.returncode != 0:
        fail(f"can't check for uncommitted changes: "
             f"{result.stderr.strip()} (--force skips this check)")
    changes = result.stdout.splitlines()
    if changes:
        listed = "\n".join(f"  {line}" for line in changes)
        fail("uncommitted changes in the build inputs; commit them first "
             "so the zip matches a commit (--force skips this check):\n"
             + listed)


def check_manifest(name: str, manifest: object) -> str:
    """Validate manifest.json and return its version_number."""
    if not isinstance(manifest, dict):
        fail(f"{name}: manifest.json must be a JSON object")
    for key, kind in (("name", str), ("version_number", str),
                      ("website_url", str), ("description", str),
                      ("dependencies", list)):
        if not isinstance(manifest.get(key), kind):
            fail(f"{name}: manifest.json needs \"{key}\" "
                 f"({'a list' if kind is list else 'a string'})")

    if manifest["name"] != name:
        fail(f"{name}: manifest name is \"{manifest['name']}\", "
             f"expected \"{name}\"")
    if not NAME_PATTERN.fullmatch(name):
        fail(f"{name}: the name may only contain letters, digits and "
             "underscores (at most 128)")
    version = manifest["version_number"]
    if not VERSION_PATTERN.fullmatch(version):
        fail(f"{name}: version_number \"{version}\" isn't "
             "Major.Minor.Patch")
    expected_url = WEBSITE_URL.format(name=name)
    if manifest["website_url"] != expected_url:
        fail(f"{name}: website_url should be {expected_url}")
    length = len(manifest["description"])
    if length > MAX_DESCRIPTION_LENGTH:
        fail(f"{name}: the description has {length} characters, "
             f"at most {MAX_DESCRIPTION_LENGTH} are allowed")

    seen: set[str] = set()
    for dependency in manifest["dependencies"]:
        match = (DEPENDENCY_PATTERN.fullmatch(dependency)
                 if isinstance(dependency, str) else None)
        if not match:
            fail(f"{name}: dependency {dependency!r} isn't "
                 "\"Team-Name-Major.Minor.Patch\"")
        package = match.group(1)
        if package == f"{TEAM}-{name}":
            fail(f"{name}: the package can't depend on itself")
        if package in seen:
            fail(f"{name}: {package} is listed twice in dependencies")
        seen.add(package)
    return version


def png_size(data: bytes) -> tuple[int, int] | None:
    """Return the size of a PNG image, or None if it isn't a PNG."""
    if (len(data) < 24 or not data.startswith(PNG_SIGNATURE)
            or data[12:16] != b"IHDR"):
        return None
    return struct.unpack(">II", data[16:24])


def check_package_files(name: str, version: str) -> None:
    """Check the store files other than the manifest."""
    folder = ROOT / "thunderstore" / name
    if not read_text(folder / "README.md").strip():
        fail(f"{name}: README.md is empty")
    changelog = read_text(folder / "CHANGELOG.md")
    heading = re.compile(rf"^##\s+{re.escape(version)}\s*$", re.MULTILINE)
    if not heading.search(changelog):
        fail(f"{name}: CHANGELOG.md has no \"## {version}\" entry")
    size = png_size(read_bytes(folder / "icon.png"))
    if size is None:
        fail(f"{name}: icon.png isn't a PNG image")
    if size != ICON_SIZE:
        fail(f"{name}: icon.png is {size[0]}x{size[1]}, it must be "
             f"{ICON_SIZE[0]}x{ICON_SIZE[1]}")


def check_plugin_version(name: str, version: str) -> None:
    """Check that PluginVersion in the plugin matches the manifest."""
    source = ROOT / "src" / name / f"{name}Plugin.cs"
    match = re.search(r'\bPluginVersion\s*=\s*"([^"]*)"', read_text(source))
    if not match:
        fail(f"{name}: no PluginVersion constant in {relative(source)}")
    if match.group(1) != version:
        fail(f"{name}: PluginVersion in {relative(source)} is "
             f"{match.group(1)}, the manifest's version_number is "
             f"{version}")


def sdk_majors(dotnet: str, env: dict[str, str]) -> list[int]:
    """Return the major versions of the SDKs this dotnet can use."""
    result = run([dotnet, "--list-sdks"], env)
    if result.returncode != 0:
        return []
    return [int(line.split(".", 1)[0]) for line in result.stdout.splitlines()
            if line[:1].isdigit()]


def find_dotnet() -> tuple[str, dict[str, str]]:
    """Find a dotnet with an SDK: the one on PATH, else ~/.dotnet.

    DOTNET_ROOT is only set for the per-user install in ~/.dotnet, which
    the modding guide uses when the system dotnet has no SDK.
    """
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_NOLOGO="1")
    candidates = []
    on_path = shutil.which("dotnet")
    if on_path:
        candidates.append((on_path, env))
    user_root = Path.home() / ".dotnet"
    user_dotnet = shutil.which("dotnet", path=str(user_root))
    if user_dotnet:
        candidates.append((user_dotnet, dict(env, DOTNET_ROOT=str(user_root))))
    for dotnet, dotnet_env in candidates:
        if any(major >= MIN_SDK_MAJOR
               for major in sdk_majors(dotnet, dotnet_env)):
            return dotnet, dotnet_env
    fail(f"no .NET SDK {MIN_SDK_MAJOR} or later found on PATH or in "
         f"{user_root}; install one from https://dotnet.microsoft.com/")


def build(name: str, version: str, dotnet: str, env: dict[str, str]) -> Path:
    """Build the plugin in Release without deploying; return the DLL."""
    project = ROOT / "src" / name / f"{name}.csproj"
    if not project.is_file():
        fail(f"{name}: {relative(project)} is missing")
    query = run([dotnet, "msbuild", str(project), "-nologo",
                 "-p:Configuration=Release", "-getProperty:TargetPath",
                 "-getProperty:Version"], env)
    try:
        if query.returncode != 0:
            raise ValueError
        properties = json.loads(query.stdout)["Properties"]
    except (ValueError, KeyError, TypeError):
        sys.stdout.write(query.stdout + query.stderr)
        fail(f"{name}: can't read the project's properties (output above)")
    if properties["Version"] != version:
        fail(f"{name}: the project builds version {properties['Version']}, "
             f"not the manifest's {version}; Directory.Build.targets "
             "should take it from the manifest")

    print(f"  building {relative(project)}")
    result = run([dotnet, "build", str(project), "-c", "Release", "-nologo",
                  "-p:DeployToProfile=false"], env)
    if result.returncode != 0:
        sys.stdout.write(result.stdout + result.stderr)
        fail(f"{name}: the build failed (output above)")
    warnings = sorted({line.strip() for line in result.stdout.splitlines()
                       if ": warning " in line})
    for warning in warnings:
        print(f"  {warning}")

    dll = Path(properties["TargetPath"])
    if not dll.is_file():
        fail(f"{name}: the build didn't produce {dll}")
    return dll


def check_no_local_paths(dll: Path, data: bytes) -> None:
    """Fail if the DLL records a local path such as C:\\Users\\..."""
    haystack = data.lower()
    for folder in {Path.home(), ROOT}:
        if len(folder.parts) < 2:
            continue  # a drive or filesystem root would match anything
        for text in {str(folder), folder.as_posix()}:
            for encoding in ("utf-8", "utf-16-le"):
                if text.lower().encode(encoding) in haystack:
                    fail(f"{relative(dll)} contains the local path "
                         f"{text}; check PathMap in Directory.Build.props")


def write_zip(path: Path, entries: list[tuple[str, bytes]]) -> None:
    """Write a reproducible zip: fixed timestamps and permissions."""
    temporary = path.with_name(path.name + ".tmp")
    try:
        with zipfile.ZipFile(temporary, "w") as archive:
            for entry_name, data in entries:
                info = zipfile.ZipInfo(entry_name, date_time=ZIP_TIMESTAMP)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.create_system = ZIP_UNIX_SYSTEM
                info.external_attr = ZIP_FILE_MODE << 16
                archive.writestr(info, data)
        os.replace(temporary, path)
    except OSError as error:
        temporary.unlink(missing_ok=True)
        fail(f"can't write {relative(path)}: {error.strerror}")


def package(name: str, force: bool, dotnet: str, env: dict[str, str]) -> None:
    """Check, build and zip one package."""
    folder = ROOT / "thunderstore" / name
    manifest_path = folder / "manifest.json"
    try:
        manifest = json.loads(read_text(manifest_path))
    except json.JSONDecodeError as error:
        fail(f"{relative(manifest_path)} isn't valid JSON: {error.msg} "
             f"(line {error.lineno}, column {error.colno})")
    version = check_manifest(name, manifest)
    print(f"{name} {version}")
    check_package_files(name, version)
    check_plugin_version(name, version)

    out = ROOT / "dist" / f"{name}-{version}.zip"
    if out.exists() and not force:
        fail(f"{relative(out)} already exists. An uploaded version can't "
             "be changed: bump the version, or delete the zip (or use "
             "--force) if it was never uploaded")

    dll = build(name, version, dotnet, env)
    dll_data = read_bytes(dll)
    check_no_local_paths(dll, dll_data)

    entries = []
    for file in PACKAGE_FILES:
        if file in TEXT_FILES:
            text = read_text(folder / file).replace("\r\n", "\n")
            entries.append((file, text.encode("utf-8")))
        else:
            entries.append((file, read_bytes(folder / file)))
    entries.append((dll.name, dll_data))

    out.parent.mkdir(exist_ok=True)
    write_zip(out, entries)
    digest = hashlib.sha256(read_bytes(out)).hexdigest()
    print(f"  wrote {relative(out)} (sha256 {digest}):")
    for entry_name, data in entries:
        print(f"  {len(data):>10,}  {entry_name}")


def main() -> int:
    """Parse the command line and package the requested packages."""
    parser = argparse.ArgumentParser(
        description="Build the Thunderstore upload zips into dist/.")
    parser.add_argument(
        "--force", action="store_true",
        help="package uncommitted changes and overwrite an existing zip "
             "(test builds only)")
    parser.add_argument(
        "packages", nargs="*", metavar="package",
        help=f"packages to build (default: all of {', '.join(PACKAGES)})")
    args = parser.parse_args()
    unknown = [name for name in args.packages if name not in PACKAGES]
    if unknown:
        parser.error(f"unknown package {', '.join(unknown)}; this repo has "
                     f"{', '.join(PACKAGES)}")

    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    try:
        if args.force:
            print("--force: not checking for uncommitted changes; existing "
                  "zips are overwritten")
        else:
            check_committed()
        dotnet, env = find_dotnet()
        for name in dict.fromkeys(args.packages or PACKAGES):
            package(name, args.force, dotnet, env)
    except PackageError as error:
        sys.stdout.flush()  # keep the error after the output before it
        print(f"error: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130
    return 0


if __name__ == "__main__":
    sys.exit(main())
