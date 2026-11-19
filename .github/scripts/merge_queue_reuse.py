#!/usr/bin/env python3
"""Decide whether a merge-queue entry may reuse a green pull-request run of the same tree.

Why this exists. A merge-queue entry builds main plus one PR, squashed. When main has not
moved since the PR's last green run, that is byte-for-byte the tree the PR run already
built. Measured over 2026-11-07..16, 18 of 53 queue entries (34%) were such a rebuild,
each one a full run with two macOS jobs.

How it decides. A PR run that finishes with EVERY job green uploads an artifact named
`green-tree-<tree>`, where <tree> is `git rev-parse HEAD^{tree}` of the commit that run
checked out (on pull_request that is the test-merge commit, the thing actually built).
This script looks that name up for the queue entry's own tree and accepts a candidate
only if its run passes every check in `verify_run`. A git tree hash covers every file,
including .github/workflows/build.yml itself, so an accepted run built the same sources
with the same workflow.

What it never does: say "reuse" on doubt. Any API error, malformed input or unexpected
shape gives reuse=false and the full build runs. The script exits 0 in every case, so a
bug in it costs a full build and never blocks the queue.

Usage (in the workflow):
    merge_queue_reuse.py --repo OWNER/NAME --tree <40-hex> \
        --workflow-path .github/workflows/build.yml \
        --skip-job "Merge-queue reuse check" \
        --required "CI script self-tests" --required ...
Reads GH_TOKEN from the environment. Writes `reuse=true|false` and `reason=...` to
$GITHUB_OUTPUT when that is set, and always prints them.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.request
from datetime import datetime, timedelta, timezone
from typing import Any, Callable

ARTIFACT_PREFIX = "green-tree-"
TREE_RE = re.compile(r"^[0-9a-f]{40}$")

Api = Callable[[str], Any]
Sleep = Callable[[float], None]

# A PR queued the moment its required checks pass can reach the queue while its run is
# still finishing the record job. Wait this long for such a run before giving up.
WAIT_POLLS = 6
WAIT_SECONDS = 10.0


def parse_time(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def verify_run(api: Api, repo: str, run_id: int, *, workflow_path: str,
               required: list[str], skip_job: str, now: datetime,
               max_age: timedelta, sleep: Sleep = time.sleep) -> str | None:
    """Return None if the run may be reused, else the reason it may not."""
    run = api(f"/repos/{repo}/actions/runs/{run_id}")
    for _ in range(WAIT_POLLS):
        if run.get("status") == "completed":
            break
        sleep(WAIT_SECONDS)
        run = api(f"/repos/{repo}/actions/runs/{run_id}")
    if run.get("path") != workflow_path:
        return f"run {run_id} is workflow {run.get('path')!r}, not {workflow_path!r}"
    if run.get("event") != "pull_request":
        return f"run {run_id} was a {run.get('event')!r} run, not pull_request"
    if run.get("status") != "completed" or run.get("conclusion") != "success":
        return (f"run {run_id} is {run.get('status')}/{run.get('conclusion')}, "
                "not completed/success")
    # A fork's run executes the fork's own workflow and can upload an artifact under any
    # name it likes, including the tree of somebody else's PR. Only a run from a branch
    # in this repository, which needed write access to push, may vouch for a tree.
    head_repo = (run.get("head_repository") or {}).get("full_name")
    if head_repo != repo:
        return f"run {run_id} came from {head_repo!r}, not a branch of {repo}"
    finished = parse_time(run["updated_at"])
    if now - finished > max_age:
        return f"run {run_id} finished {now - finished} ago, older than {max_age}"

    jobs = api(f"/repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100")
    by_name: dict[str, list[str | None]] = {}
    for job in jobs.get("jobs", []):
        by_name.setdefault(job.get("name"), []).append(job.get("conclusion"))
    for name in required:
        if by_name.get(name) != ["success"]:
            return f"run {run_id}: required job {name!r} concluded {by_name.get(name)}"
    # Stronger than the required list: a skipped job counts as success in a run's
    # conclusion, so check that nothing other than the reuse check itself was skipped.
    for name, conclusions in by_name.items():
        allowed = {"skipped"} if name == skip_job else {"success"}
        if any(c not in allowed for c in conclusions):
            return f"run {run_id}: job {name!r} concluded {conclusions}"
    return None


def decide(api: Api, repo: str, tree: str, *, workflow_path: str, required: list[str],
           skip_job: str, now: datetime, max_age: timedelta,
           sleep: Sleep = time.sleep) -> tuple[bool, str]:
    if not TREE_RE.match(tree):
        return False, f"tree {tree!r} is not a 40-hex git object id"
    if not required:
        return False, "no required jobs were named, so nothing can be verified"
    name = ARTIFACT_PREFIX + tree
    listing = api(f"/repos/{repo}/actions/artifacts?name={name}&per_page=100")
    artifacts = [a for a in listing.get("artifacts", []) if a.get("name") == name]
    if not artifacts:
        return False, f"no green pull-request run recorded tree {tree}"
    artifacts.sort(key=lambda a: a.get("created_at", ""), reverse=True)
    reasons = []
    for artifact in artifacts:
        if artifact.get("expired"):
            reasons.append(f"artifact {artifact.get('id')} has expired")
            continue
        run_id = (artifact.get("workflow_run") or {}).get("id")
        if not isinstance(run_id, int):
            reasons.append(f"artifact {artifact.get('id')} names no workflow run")
            continue
        why_not = verify_run(api, repo, run_id, workflow_path=workflow_path,
                             required=required, skip_job=skip_job, now=now,
                             max_age=max_age, sleep=sleep)
        if why_not is None:
            return True, (f"tree {tree} was built green by pull-request run {run_id} "
                          f"(https://github.com/{repo}/actions/runs/{run_id})")
        reasons.append(why_not)
    return False, "; ".join(reasons)


def github_api(token: str) -> Api:
    def get(path: str) -> Any:
        request = urllib.request.Request(
            "https://api.github.com" + path,
            headers={"Authorization": f"Bearer {token}",
                     "Accept": "application/vnd.github+json",
                     "X-GitHub-Api-Version": "2022-11-28"})
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    return get


def emit(reuse: bool, reason: str) -> None:
    reason = reason.replace("\n", " ")
    print(f"reuse={'true' if reuse else 'false'}")
    print(f"reason={reason}")
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as handle:
            handle.write(f"reuse={'true' if reuse else 'false'}\n")
            handle.write(f"reason={reason}\n")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            verdict = "REUSED, heavy jobs skipped" if reuse else "full build"
            handle.write(f"### Merge-queue reuse: {verdict}\n\n{reason}\n")


def main(argv: list[str] | None = None, api: Api | None = None,
         now: datetime | None = None, sleep: Sleep = time.sleep) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True)
    parser.add_argument("--tree", required=True)
    parser.add_argument("--workflow-path", required=True)
    parser.add_argument("--skip-job", required=True)
    parser.add_argument("--required", action="append", default=[])
    parser.add_argument("--max-age-hours", type=float, default=24.0)
    args = parser.parse_args(argv)
    try:
        if api is None:
            token = os.environ.get("GH_TOKEN", "")
            if not token:
                emit(False, "GH_TOKEN is not set, so no run can be looked up")
                return 0
            api = github_api(token)
        reuse, reason = decide(
            api, args.repo, args.tree.strip(), workflow_path=args.workflow_path,
            required=args.required, skip_job=args.skip_job,
            now=now or datetime.now(timezone.utc),
            max_age=timedelta(hours=args.max_age_hours), sleep=sleep)
    except Exception as error:  # noqa: BLE001 — any doubt means a full build
        reuse, reason = False, f"lookup failed ({type(error).__name__}: {error})"
    emit(reuse, reason)
    return 0


if __name__ == "__main__":
    sys.exit(main())
