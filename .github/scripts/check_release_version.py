#!/usr/bin/env python3
"""Checks a release tag against the version the tree says it is (#344).

    python3 check_release_version.py v3.4.0-beta.1 [--root <repo root>]

The tag must be v<DaqifiAppVersion>, optionally with a prerelease suffix (v3.4.0-beta.1),
and Daqifi.Avalonia.Desktop/app.manifest must say <DaqifiAppVersion>.0. A tag that disagrees
with the tree would ship packages the updater compares wrongly and a Sentry release with the
wrong name; app.manifest repeats the number and nothing else keeps it in step.

It also derives the MSI ProductVersion (msi_version=). Windows Installer compares only the
first three fields, so vpk's default of <major>.<minor>.<patch>.0 gives every 3.4.0 beta and
3.4.0 itself the same version, and an MSI push cannot tell them apart. Instead each tag gets a
build field that grows with every release, in the same order SemVer (and so the in-app
updater) puts them:

    build = patch * 100 + rank

    rank  1-29   -alpha.1 .. -alpha.29
    rank 31-69   -beta.1  .. -beta.39
    rank 71-98   -rc.1    .. -rc.28
    rank 99      the stable release

So 3.4.0-alpha.2 -> 3.4.2, 3.4.0-beta.3 -> 3.4.33, 3.4.0-rc.1 -> 3.4.71, 3.4.0 -> 3.4.99 and
3.4.1-beta.1 -> 3.4.131. A tag the scheme cannot place (another label, a number outside its
band, a patch above 654, a major or minor above 255) is refused rather than given a version
that would sort wrongly against its neighbours.

On success prints GitHub Actions outputs (tag=, version=, prerelease=, msi_version=) and exits
0. On any mismatch prints a ::error:: line and exits 1.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

TAG = re.compile(r"^v(?P<base>\d+\.\d+\.\d+)(?P<pre>-[0-9A-Za-z][0-9A-Za-z.-]*)?$")


# Label -> (first rank, highest number). Rank = first rank + number - 1, so each band ends
# below the next one starts: alpha 1-29, beta 31-69, rc 71-98, stable 99.
PRERELEASE_BANDS = {"alpha": (1, 29), "beta": (31, 39), "rc": (71, 28)}
STABLE_RANK = 99
MAX_PATCH = 654  # 654 * 100 + 99 = 65499, the largest that fits MSI's 65535 build field
PRERELEASE = re.compile(r"^-(?P<label>[a-z]+)\.(?P<number>[1-9][0-9]*)$")


def msi_version(base: str, pre: str) -> str:
    """The MSI ProductVersion for a release, ordered the same way SemVer orders the tags."""
    major, minor, patch = (int(part) for part in base.split("."))
    if major > 255 or minor > 255:
        raise ValueError(f"version {base}: an MSI ProductVersion allows at most 255 for major and minor")
    if patch > MAX_PATCH:
        raise ValueError(f"version {base}: the MSI version scheme fits patch numbers up to {MAX_PATCH}")
    if not pre:
        rank = STABLE_RANK
    else:
        match = PRERELEASE.match(pre)
        band = PRERELEASE_BANDS.get(match.group("label")) if match else None
        if band is None:
            raise ValueError(
                f"prerelease '{pre[1:]}' cannot be given an MSI version. Use alpha.N, beta.N or rc.N "
                "(see the scheme in check_release_version.py).")
        first, highest = band
        number = int(match.group("number"))
        if number > highest:
            raise ValueError(
                f"prerelease '{pre[1:]}': the MSI version scheme fits {match.group('label')}.1 to "
                f"{match.group('label')}.{highest}. Move to the next label or the next patch.")
        rank = first + number - 1
    return f"{major}.{minor}.{patch * 100 + rank}"


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


def check(tag: str, root: str) -> tuple[str, str, bool, str]:
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

    return tag, base + pre, bool(pre), msi_version(base, pre)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("tag")
    parser.add_argument("--root", default=os.getcwd())
    args = parser.parse_args(argv)
    try:
        tag, version, prerelease, msi = check(args.tag, args.root)
    except (ValueError, OSError, ET.ParseError) as error:
        print(f"::error::{error}")
        return 1
    print(f"tag={tag}")
    print(f"version={version}")
    print(f"prerelease={'true' if prerelease else 'false'}")
    print(f"msi_version={msi}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
