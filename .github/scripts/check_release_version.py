#!/usr/bin/env python3
"""Checks a release tag against the version the tree says it is (#344).

    python3 check_release_version.py v3.4.0-beta.1 [--root <repo root>]

The tag must be v<DaqifiAppVersion>, optionally with a prerelease suffix (v3.4.0-beta.1),
and Daqifi.Avalonia.Desktop/app.manifest must say <DaqifiAppVersion>.0. A tag that disagrees
with the tree would ship packages the updater compares wrongly and a Sentry release with the
wrong name; app.manifest repeats the number and nothing else keeps it in step.

On success prints GitHub Actions outputs (tag=, version=, prerelease=) and exits 0. On any
mismatch prints a ::error:: line and exits 1.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

TAG = re.compile(r"^v(?P<base>\d+\.\d+\.\d+)(?P<pre>-[0-9A-Za-z][0-9A-Za-z.-]*)?$")


def app_version(root: str) -> str:
    props = ET.parse(os.path.join(root, "Directory.Build.props")).getroot()
    values = [e.text.strip() for e in props.iter("DaqifiAppVersion") if e.text and e.text.strip()]
    if len(values) != 1:
        raise ValueError(f"expected one DaqifiAppVersion in Directory.Build.props, found {len(values)}")
    return values[0]


def manifest_version(root: str) -> str:
    path = os.path.join(root, "Daqifi.Avalonia.Desktop", "app.manifest")
    tree = ET.parse(path).getroot()
    for element in tree.iter():
        if element.tag.split("}")[-1] == "assemblyIdentity":
            return element.attrib.get("version", "")
    raise ValueError("app.manifest has no assemblyIdentity")


def check(tag: str, root: str) -> tuple[str, str, bool]:
    match = TAG.match(tag)
    if not match:
        raise ValueError(f"tag '{tag}' is not v<major>.<minor>.<patch>[-prerelease]")
    base, pre = match.group("base"), match.group("pre") or ""

    expected = app_version(root)
    if base != expected:
        raise ValueError(
            f"tag '{tag}' does not match DaqifiAppVersion {expected} in Directory.Build.props. "
            "Bump the property (or fix the tag) so the build and the tag name the same release.")

    manifest = manifest_version(root)
    if manifest != f"{expected}.0":
        raise ValueError(
            f"Daqifi.Avalonia.Desktop/app.manifest says version=\"{manifest}\", expected "
            f"\"{expected}.0\" to match DaqifiAppVersion.")

    return tag, base + pre, bool(pre)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("tag")
    parser.add_argument("--root", default=os.getcwd())
    args = parser.parse_args(argv)
    try:
        tag, version, prerelease = check(args.tag, args.root)
    except (ValueError, OSError, ET.ParseError) as error:
        print(f"::error::{error}")
        return 1
    print(f"tag={tag}")
    print(f"version={version}")
    print(f"prerelease={'true' if prerelease else 'false'}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
