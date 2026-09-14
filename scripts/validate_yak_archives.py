#!/usr/bin/env python3
"""Validate Instant Mesh+ Yak archives before any server push."""

from __future__ import annotations

import argparse
import json
import re
import sys
import zipfile
from pathlib import Path
from typing import Any


VERSION_PATTERN = r"\d+\.\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.-]+)?"
DISTRIBUTION_PATTERN = r"(?:rh\d+(?:_\d+)?|any)"
PLATFORM_PATTERN = r"(?:any|win|mac)"
MACH_CPU_TYPES = {
    0x01000007: "x86_64",
    0x0100000C: "arm64",
}


def manifest_scalar(text: str, key: str) -> str | None:
    pattern = rf"(?im)^\s*{re.escape(key)}\s*:\s*(.+?)\s*$"
    match = re.search(pattern, text)
    if not match:
        return None
    value = match.group(1).strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {"'", '"'}:
        value = value[1:-1]
    return value.strip()


def base_version_matches(version: str, base: str) -> bool:
    return version == base or version.startswith((base + ".", base + "-", base + "+"))


def platform_entries(values: list[str], option: str) -> dict[str, set[str]]:
    result: dict[str, set[str]] = {}
    for value in values:
        platform, separator, entry = value.partition(":")
        platform = platform.strip().lower()
        entry = entry.replace("\\", "/").strip("/").casefold()
        if not separator or platform not in {"win", "mac", "any"} or not entry:
            raise ValueError(f"{option} must use <platform>:<archive-path>; received {value!r}")
        result.setdefault(platform, set()).add(entry)
    return result


def macho_architectures(data: bytes) -> set[str]:
    if len(data) < 8:
        return set()
    magic = int.from_bytes(data[:4], "big")
    if magic in {0xCAFEBABE, 0xCAFEBABF}:
        count = int.from_bytes(data[4:8], "big")
        entry_size = 20 if magic == 0xCAFEBABE else 32
        architectures: set[str] = set()
        for index in range(count):
            offset = 8 + index * entry_size
            if offset + 4 > len(data):
                return set()
            cpu_type = int.from_bytes(data[offset : offset + 4], "big")
            name = MACH_CPU_TYPES.get(cpu_type)
            if name:
                architectures.add(name)
        return architectures

    little_magic = int.from_bytes(data[:4], "little")
    if little_magic in {0xFEEDFACE, 0xFEEDFACF}:
        cpu_type = int.from_bytes(data[4:8], "little")
    elif magic in {0xFEEDFACE, 0xFEEDFACF}:
        cpu_type = int.from_bytes(data[4:8], "big")
    else:
        return set()
    name = MACH_CPU_TYPES.get(cpu_type)
    return {name} if name else set()


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Validate local .yak archives before a test or public push."
    )
    parser.add_argument("--root", required=True, help="Directory containing .yak archives")
    parser.add_argument("--package-name", required=True, help="Expected manifest name")
    parser.add_argument("--filename-prefix", help="Expected archive filename prefix")
    parser.add_argument("--base-version", help="Expected X.Y.Z base version")
    parser.add_argument("--expected-version", help="Exact expected archive version")
    parser.add_argument(
        "--expected-distribution",
        action="append",
        default=[],
        help="Expected app tag such as rh8_32; repeat for multiple tags",
    )
    parser.add_argument(
        "--expected-platform",
        action="append",
        default=[],
        help="Expected platform such as win; repeat for multiple platforms",
    )
    parser.add_argument("--expected-guid", help="Expected guid: keyword in the manifest")
    parser.add_argument(
        "--required-entry",
        action="append",
        default=[],
        help="Required archive-relative path; repeat for multiple entries",
    )
    parser.add_argument(
        "--platform-required-entry",
        action="append",
        default=[],
        help="Required entry for one platform as win:path or mac:path; repeat as needed",
    )
    parser.add_argument(
        "--platform-prohibited-entry",
        action="append",
        default=[],
        help="Prohibited entry for one platform as win:path or mac:path; repeat as needed",
    )
    parser.add_argument(
        "--platform-executable-entry",
        action="append",
        default=[],
        help="Entry that must retain a Unix executable bit as mac:path",
    )
    parser.add_argument(
        "--platform-universal-entry",
        action="append",
        default=[],
        help="Mach-O entry that must contain arm64 and x86_64 as mac:path",
    )
    parser.add_argument(
        "--write-list",
        help="Write validated absolute archive paths to this file on success",
    )
    args = parser.parse_args()

    root = Path(args.root).expanduser().resolve()
    if not root.is_dir():
        print(f"Archive root is not a directory: {root}", file=sys.stderr)
        return 2

    prefix = args.filename_prefix or re.sub(
        r"[^a-z0-9]+", "-", args.package_name.lower()
    ).strip("-")
    filename_regex = re.compile(
        rf"^{re.escape(prefix)}-(?P<version>{VERSION_PATTERN})-"
        rf"(?P<distribution>{DISTRIBUTION_PATTERN})-"
        rf"(?P<platform>{PLATFORM_PATTERN})\.yak$",
        re.IGNORECASE,
    )
    archives = sorted(root.glob("*.yak"), key=lambda path: path.name.lower())
    errors: list[str] = []
    reports: list[dict[str, Any]] = []

    if not archives:
        errors.append(f"No .yak archives found in {root}")

    required_entries = {entry.replace("\\", "/").strip("/").casefold() for entry in args.required_entry}
    try:
        required_by_platform = platform_entries(
            args.platform_required_entry, "--platform-required-entry"
        )
        prohibited_by_platform = platform_entries(
            args.platform_prohibited_entry, "--platform-prohibited-entry"
        )
        executable_by_platform = platform_entries(
            args.platform_executable_entry, "--platform-executable-entry"
        )
        universal_by_platform = platform_entries(
            args.platform_universal_entry, "--platform-universal-entry"
        )
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 2
    expected_guid = args.expected_guid.casefold() if args.expected_guid else None

    for archive in archives:
        report: dict[str, Any] = {"file": str(archive), "errors": []}
        match = filename_regex.fullmatch(archive.name)
        if not match:
            report["errors"].append(
                f"Filename does not match {prefix}-<version>-<distribution>-<platform>.yak"
            )
            reports.append(report)
            continue

        version = match.group("version")
        distribution = match.group("distribution").lower()
        platform = match.group("platform").lower()
        report.update(
            {"version": version, "distribution": distribution, "platform": platform}
        )
        if args.base_version and not base_version_matches(version, args.base_version):
            report["errors"].append(
                f"Version {version} does not match base {args.base_version}"
            )
        if args.expected_version and version != args.expected_version:
            report["errors"].append(
                f"Version {version} does not exactly match {args.expected_version}"
            )

        try:
            with zipfile.ZipFile(archive) as yak:
                entries = [
                    name.replace("\\", "/").strip("/")
                    for name in yak.namelist()
                    if not name.endswith(("/", "\\"))
                ]
                entry_set = {entry.casefold() for entry in entries}
                entry_lookup = {entry.casefold(): entry for entry in entries}
                report["entries"] = entries
                platform_required = required_entries | required_by_platform.get(platform, set())
                missing_entries = sorted(platform_required - entry_set)
                if missing_entries:
                    report["errors"].append(
                        "Missing required entries: " + ", ".join(missing_entries)
                    )
                prohibited_entries = sorted(
                    prohibited_by_platform.get(platform, set()) & entry_set
                )
                if prohibited_entries:
                    report["errors"].append(
                        "Prohibited platform entries are present: "
                        + ", ".join(prohibited_entries)
                    )
                debug_entries = sorted(
                    entry for entry in entries if entry.casefold().endswith(".pdb")
                )
                if debug_entries:
                    report["errors"].append(
                        "Debug symbols must not be packaged: " + ", ".join(debug_entries)
                    )

                for executable_entry in sorted(executable_by_platform.get(platform, set())):
                    actual_entry = entry_lookup.get(executable_entry)
                    if actual_entry is None:
                        continue
                    unix_mode = yak.getinfo(actual_entry).external_attr >> 16
                    if unix_mode & 0o111 == 0:
                        report["errors"].append(
                            f"Entry {actual_entry} does not retain a Unix executable bit"
                        )

                for universal_entry in sorted(universal_by_platform.get(platform, set())):
                    actual_entry = entry_lookup.get(universal_entry)
                    if actual_entry is None:
                        continue
                    architectures = macho_architectures(yak.read(actual_entry))
                    if architectures != {"arm64", "x86_64"}:
                        rendered = ", ".join(sorted(architectures)) or "none"
                        report["errors"].append(
                            f"Entry {actual_entry} is not arm64+x86_64 universal; found {rendered}"
                        )

                root_manifests = [
                    entry for entry in entries if entry.casefold() in {"manifest.yml", "manifest.yaml"}
                ]
                if len(root_manifests) != 1:
                    report["errors"].append(
                        f"Expected exactly one root manifest, found {len(root_manifests)}"
                    )
                else:
                    manifest_text = yak.read(root_manifests[0]).decode(
                        "utf-8-sig", errors="replace"
                    )
                    manifest_name = manifest_scalar(manifest_text, "name")
                    manifest_version = manifest_scalar(manifest_text, "version")
                    manifest_platform = manifest_scalar(manifest_text, "platform")
                    manifest_icon = manifest_scalar(manifest_text, "icon")
                    report["manifest"] = {
                        "path": root_manifests[0],
                        "name": manifest_name,
                        "version": manifest_version,
                        "platform": manifest_platform,
                        "icon": manifest_icon,
                    }
                    if re.search(r"(?i)(\$version\b|__[A-Z0-9_]+__)", manifest_text):
                        report["errors"].append("Manifest contains an unresolved placeholder")
                    if (
                        manifest_name is None
                        or manifest_name.casefold() != args.package_name.casefold()
                    ):
                        report["errors"].append(
                            f"Manifest name {manifest_name!r} != {args.package_name!r}"
                        )
                    if manifest_version != version:
                        report["errors"].append(
                            f"Manifest version {manifest_version!r} != filename version {version!r}"
                        )
                    if manifest_platform is None or manifest_platform.lower() != platform:
                        report["errors"].append(
                            f"Manifest platform {manifest_platform!r} != filename platform {platform!r}"
                        )
                    if manifest_icon:
                        icon_entry = manifest_icon.replace("\\", "/").strip("/")
                        if icon_entry.casefold() not in entry_set:
                            report["errors"].append(
                                f"Manifest icon {manifest_icon!r} is not present in the archive"
                            )
                    if expected_guid and f"guid:{expected_guid}" not in manifest_text.casefold():
                        report["errors"].append(
                            f"Manifest does not contain guid:{args.expected_guid}"
                        )
        except (OSError, zipfile.BadZipFile, KeyError) as exc:
            report["errors"].append(f"Cannot inspect archive: {exc}")

        reports.append(report)

    valid_reports = [report for report in reports if "version" in report]
    versions = sorted({report["version"] for report in valid_reports})
    distributions = sorted({report["distribution"] for report in valid_reports})
    platforms = sorted({report["platform"] for report in valid_reports})
    if len(versions) > 1:
        errors.append(f"Archives do not share one version: {', '.join(versions)}")

    archive_keys = [
        (report["version"], report["distribution"], report["platform"])
        for report in valid_reports
    ]
    duplicate_keys = sorted({key for key in archive_keys if archive_keys.count(key) > 1})
    for version, distribution, platform in duplicate_keys:
        errors.append(
            "Duplicate archive distribution: "
            f"version={version}, distribution={distribution}, platform={platform}"
        )

    expected_distributions = sorted(
        {value.lower() for value in args.expected_distribution}
    )
    if expected_distributions and distributions != expected_distributions:
        errors.append(
            "Distribution set mismatch: "
            f"expected {', '.join(expected_distributions)}; found {', '.join(distributions)}"
        )

    expected_platforms = sorted({value.lower() for value in args.expected_platform})
    if expected_platforms and platforms != expected_platforms:
        errors.append(
            "Platform set mismatch: "
            f"expected {', '.join(expected_platforms)}; found {', '.join(platforms)}"
        )
    if expected_distributions and expected_platforms:
        expected_pairs = {
            (distribution, platform)
            for distribution in expected_distributions
            for platform in expected_platforms
        }
        actual_pairs = {
            (report["distribution"], report["platform"])
            for report in valid_reports
        }
        if actual_pairs != expected_pairs:
            render = lambda pairs: ", ".join(
                f"{distribution}/{platform}" for distribution, platform in sorted(pairs)
            )
            errors.append(
                "Distribution/platform pair mismatch: "
                f"expected {render(expected_pairs)}; found {render(actual_pairs)}"
            )

    for report in reports:
        errors.extend(
            f"{Path(report['file']).name}: {message}" for message in report["errors"]
        )

    result = {
        "ok": not errors,
        "root": str(root),
        "package_name": args.package_name,
        "filename_prefix": prefix,
        "versions": versions,
        "distributions": distributions,
        "platforms": platforms,
        "archives": reports,
        "errors": errors,
    }
    print(json.dumps(result, indent=2))

    if not errors and args.write_list:
        list_path = Path(args.write_list).expanduser().resolve()
        list_path.parent.mkdir(parents=True, exist_ok=True)
        list_path.write_text(
            "".join(f"{archive.resolve()}\n" for archive in archives), encoding="utf-8"
        )

    return 0 if not errors else 1


if __name__ == "__main__":
    raise SystemExit(main())
