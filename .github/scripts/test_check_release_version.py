#!/usr/bin/env python3
"""Self-test for check_release_version.py.

The guard's value is that it FAILS when the tag and the tree disagree, so the failure paths
are what this exercises, not just the happy one. Run directly; exits 0 if every case behaves.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
GUARD = os.path.join(HERE, "check_release_version.py")
REPO = os.path.dirname(os.path.dirname(HERE))

PROPS = """<Project>
  <!-- a comment mentioning <DaqifiAppVersion> must not count -->
  <PropertyGroup><DaqifiAppVersion>{version}</DaqifiAppVersion></PropertyGroup>
</Project>
"""
MANIFEST = """<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="{version}" name="Daqifi.Avalonia.Desktop"/>
</assembly>
"""


def tree(app: str, manifest: str) -> str:
    root = tempfile.mkdtemp(prefix="release-version-")
    with open(os.path.join(root, "Directory.Build.props"), "w", encoding="utf-8") as f:
        f.write(PROPS.format(version=app))
    os.makedirs(os.path.join(root, "Daqifi.Avalonia.Desktop"))
    with open(os.path.join(root, "Daqifi.Avalonia.Desktop", "app.manifest"), "w", encoding="utf-8") as f:
        f.write(MANIFEST.format(version=manifest))
    return root


def run(tag: str, root: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([sys.executable, GUARD, tag, "--root", root], capture_output=True, text=True)


failures: list[str] = []


def expect(name: str, ok: bool) -> None:
    print(("PASS " if ok else "FAIL ") + name)
    if not ok:
        failures.append(name)


good = tree("3.4.0", "3.4.0.0")

r = run("v3.4.0", good)
expect("stable tag passes", r.returncode == 0
       and "version=3.4.0" in r.stdout and "prerelease=false" in r.stdout)

r = run("v3.4.0-beta.1", good)
expect("prerelease tag passes and is flagged", r.returncode == 0
       and "version=3.4.0-beta.1" in r.stdout and "prerelease=true" in r.stdout)

expect("tag on a tree with another version fails", run("v3.5.0", good).returncode == 1)
expect("tag without the v fails", run("3.4.0", good).returncode == 1)
expect("two-part tag fails", run("v3.4", good).returncode == 1)
expect("junk after the version fails", run("v3.4.0x", good).returncode == 1)
expect("stale app.manifest fails", run("v3.4.0", tree("3.4.0", "3.3.0.0")).returncode == 1)

# --- MSI version (#505 review) ---------------------------------------------------------------
# Windows Installer compares major.minor.build only. Every tag must map to a strictly larger
# MSI version than every tag SemVer puts before it, or an MSI push cannot upgrade in place.

r = run("v3.4.0-beta.3", good)
expect("the MSI version reaches the outputs", r.returncode == 0 and "msi_version=3.4.33" in r.stdout)
expect("a stable tag's MSI version", "msi_version=3.4.99" in run("v3.4.0", good).stdout)
expect("an unorderable prerelease fails the guard", run("v3.4.0-preview.1", good).returncode == 1)

sys.dont_write_bytecode = True  # no __pycache__ left in the checkout
sys.path.insert(0, HERE)
import check_release_version  # noqa: E402

msi = check_release_version.msi_version


def key(version: str) -> tuple[int, int, int]:
    return tuple(int(part) for part in version.split("."))  # what Windows Installer compares


def split(tag: str) -> tuple[str, str]:
    base, _, pre = tag.partition("-")
    return base, ("-" + pre if pre else "")


# Tags in SemVer order, including every band edge and the step across patch and minor.
ORDER = [
    "3.3.0-alpha.1", "3.3.0-alpha.2", "3.3.0-alpha.29",
    "3.3.0-beta.1", "3.3.0-beta.2", "3.3.0-beta.9", "3.3.0-beta.10", "3.3.0-beta.39",
    "3.3.0-rc.1", "3.3.0-rc.28",
    "3.3.0",
    "3.3.1-alpha.1", "3.3.1-beta.1", "3.3.1",
    "3.3.10-beta.1", "3.3.10",
    "3.3.654-beta.1", "3.3.654",
    "3.4.0-alpha.1", "3.4.0", "4.0.0-beta.1", "4.0.0", "255.255.654",
]
versions = [msi(*split(t)) for t in ORDER]
pairs = list(zip(ORDER, versions))
bad = [(a, b) for a, b in zip(pairs, pairs[1:]) if not key(a[1]) < key(b[1])]
expect("every later tag gets a strictly larger MSI version", not bad)
if bad:
    print("   out of order:", bad)
expect("each MSI version fits MSI's field limits",
       all(k[0] <= 255 and k[1] <= 255 and k[2] <= 65535 for k in map(key, versions)))
expect("beta.10 sorts after beta.9 (numeric, not text)",
       key(msi("3.3.0", "-beta.10")) > key(msi("3.3.0", "-beta.9")))
expect("the published betas keep their order",
       key(msi("3.3.0", "-beta.1")) < key(msi("3.3.0", "-beta.2")) < key(msi("3.3.0", "")))
expect("exact values: alpha.1, beta.1, rc.1, stable",
       [msi("3.4.0", p) for p in ("-alpha.1", "-beta.1", "-rc.1", "")]
       == ["3.4.1", "3.4.31", "3.4.71", "3.4.99"])


def refused(base: str, pre: str) -> bool:
    try:
        msi(base, pre)
    except ValueError:
        return True
    return False


for base, pre, why in [
    ("3.3.0", "-alpha.30", "alpha past its band"),
    ("3.3.0", "-beta.40", "beta past its band"),
    ("3.3.0", "-rc.29", "rc past its band"),
    ("3.3.0", "-beta.0", "number zero"),
    ("3.3.0", "-beta.01", "leading zero"),
    ("3.3.0", "-beta", "no number"),
    ("3.3.0", "-Beta.1", "capitalised label"),
    ("3.3.0", "-preview.1", "unknown label"),
    ("3.3.0", "-beta.1.2", "extra identifier"),
    ("3.3.0", "-msi.1", "local test label"),
    ("3.3.655", "", "patch too large for the build field"),
    ("256.0.0", "", "major above 255"),
    ("3.256.0", "", "minor above 255"),
]:
    expect(f"refused: {why} ({base}{pre})", refused(base, pre))

# The real tree must agree with itself, or the next release tag would fail on arrival. Tagged
# with the tree's own version so the run gets past the tag comparison and reaches the manifest
# check; a tag that failed earlier would pass this row whatever app.manifest said.
real = check_release_version.app_version(REPO)
r = run(f"v{real}", REPO)
expect(f"the real tree passes for its own version v{real}", r.returncode == 0)

sys.exit(1 if failures else 0)
