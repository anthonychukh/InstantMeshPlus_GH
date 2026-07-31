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
                report["entries"] = entries
                missing_entries = sorted(required_entries - entry_set)
                if missing_entries:
                    report["errors"].append(
                        "Missing required entries: " + ", ".join(missing_entries)
                    )
                debug_entries = sorted(
                    entry for entry in entries if entry.casefold().endswith(".pdb")
                )
                if debug_entries:
                    report["errors"].append(
                        "Debug symbols must not be packaged: " + ", ".join(debug_entries)
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
                    report["manifest"] = {
                        "path": root_manifests[0],
                        "name": manifest_name,
                        "version": manifest_version,
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
