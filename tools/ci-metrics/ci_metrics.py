#!/usr/bin/env python3
"""Measure the Build workflow: time to green, per-job time, macOS queueing, runner-minutes.

The numbers the CI notes in .github/workflows/build.yml and .github/merge-queue/README.md
quote come from this script. Re-run it before changing CI on the strength of a cost or
speed argument, and again after, over windows that do not straddle the change.

    python3 tools/ci-metrics/ci_metrics.py --since 2026-11-07 [--until 2026-11-17]

Needs an authenticated `gh` on PATH. Read-only: it only calls GET endpoints.

What it measures, and how:
  * wall clock: run created -> last job completed, successful runs only. This is the
    time a PR author waits for a green tick, including runner queueing.
  * job duration: started -> completed; job queue: created -> started.
  * runner-minutes: the sum of job durations, split Linux / macOS. The repository is
    public, so these cost no money. They are the measure of runner CAPACITY, and macOS
    capacity is the scarce one (5 concurrent jobs per Free-plan org).
  * queue reuse: merge_group runs that succeeded with `iOS head` skipped, i.e. runs
    where the queue-reuse check verified a green PR run of the same tree.
Only the latest attempt of each run is counted.
"""

from __future__ import annotations

import argparse
import collections
import json
import math
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime


def gh(path: str) -> list:
    out = subprocess.run(["gh", "api", "--paginate", "--slurp", path],
                         check=True, capture_output=True, text=True).stdout
    return json.loads(out)


def t(value: str | None) -> datetime | None:
    return datetime.fromisoformat(value.replace("Z", "+00:00")) if value else None


def pct(values: list[float], p: float) -> float:
    if not values:
        return float("nan")
    values = sorted(values)
    k = (len(values) - 1) * p
    lo, hi = math.floor(k), math.ceil(k)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)


def dist(values: list[float]) -> str:
    return (f"n={len(values):3d}  p50={pct(values, .5):5.1f}  p90={pct(values, .9):5.1f}"
            f"  max={max(values) if values else float('nan'):5.1f}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", default="daqifi/daqifi-app")
    parser.add_argument("--workflow", default="build.yml")
    parser.add_argument("--since", required=True, help="YYYY-MM-DD, inclusive")
    parser.add_argument("--until", help="YYYY-MM-DD, exclusive")
    args = parser.parse_args()

    created = f">={args.since}" if not args.until else f"{args.since}..{args.until}"
    pages = gh(f"repos/{args.repo}/actions/workflows/{args.workflow}/runs"
               f"?created={created}&per_page=100")
    runs = [r for page in pages for r in page["workflow_runs"]]
    if args.until:  # the range form is inclusive at both ends; make --until exclusive
        runs = [r for r in runs if r["created_at"][:10] < args.until]
    if not runs:
        print("no runs in that window")
        return 1

    def jobs_of(run: dict) -> list[dict]:
        pages = gh(f"repos/{args.repo}/actions/runs/{run['id']}/jobs?filter=latest&per_page=100")
        return [j for page in pages for j in page["jobs"]]

    with ThreadPoolExecutor(max_workers=8) as pool:
        jobs = dict(zip((r["id"] for r in runs), pool.map(jobs_of, runs)))

    print(f"{args.repo} {args.workflow}, {args.since} .. {args.until or 'now'}: "
          f"{len(runs)} runs\n")
    for event in ("pull_request", "merge_group", "push", "workflow_dispatch"):
        selected = [r for r in runs if r["event"] == event]
        if not selected:
            continue
        outcome = collections.Counter(r["conclusion"] for r in selected)
        print(f"== {event}: {dict(outcome)}")
        walls, linux, mac = [], 0.0, 0.0
        per_job: dict[str, dict[str, list[float]]] = collections.defaultdict(
            lambda: {"dur": [], "queue": []})
        reused = 0
        for run in selected:
            done = [j for j in jobs[run["id"]] if j["started_at"] and j["completed_at"]
                    and j["conclusion"] != "skipped"]
            for job in done:
                minutes = (t(job["completed_at"]) - t(job["started_at"])).total_seconds() / 60
                if "macos" in ",".join(job["labels"]):
                    mac += minutes
                else:
                    linux += minutes
            if run["conclusion"] != "success" or not done:
                continue
            if event == "merge_group" and any(
                    j["name"] == "iOS head" and j["conclusion"] == "skipped"
                    for j in jobs[run["id"]]):
                reused += 1
            walls.append((max(t(j["completed_at"]) for j in done)
                          - t(run["created_at"])).total_seconds() / 60)
            for job in done:
                per_job[job["name"]]["dur"].append(
                    (t(job["completed_at"]) - t(job["started_at"])).total_seconds() / 60)
                per_job[job["name"]]["queue"].append(
                    (t(job["started_at"]) - t(job["created_at"])).total_seconds() / 60)
        print(f"   time to green (min)      {dist(walls)}")
        print(f"   runner-minutes, all runs  linux={linux:.0f}  macOS={mac:.0f}"
              f"  per run: linux={linux / len(selected):.1f} macOS={mac / len(selected):.1f}")
        if event == "merge_group":
            print(f"   reused a green PR run     {reused} of {outcome.get('success', 0)}"
                  " successful entries")
        for name, v in sorted(per_job.items(), key=lambda kv: -pct(kv[1]["dur"], .5)):
            print(f"   {name[:40]:40s} dur {dist(v['dur'])} | queue p90={pct(v['queue'], .9):4.1f}")
        print()

    mac_jobs = [j for js in jobs.values() for j in js
                if j["started_at"] and "macos" in ",".join(j["labels"])]
    waited = [j for j in mac_jobs
              if (t(j["started_at"]) - t(j["created_at"])).total_seconds() > 60]
    print(f"macOS jobs that waited > 1 min for a runner: {len(waited)} of {len(mac_jobs)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
