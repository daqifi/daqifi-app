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

# The real tree must agree with itself, or the next release tag would fail on arrival. Tagged
# with the tree's own version so the run gets past the tag comparison and reaches the manifest
# check; a tag that failed earlier would pass this row whatever app.manifest said.
sys.dont_write_bytecode = True  # no __pycache__ left in the checkout
sys.path.insert(0, HERE)
import check_release_version  # noqa: E402

real = check_release_version.app_version(REPO)
r = run(f"v{real}", REPO)
expect(f"the real tree passes for its own version v{real}", r.returncode == 0)

sys.exit(1 if failures else 0)
