#!/usr/bin/env python3
"""Self-test for merge_queue_reuse.py.

The script's value is entirely in its REFUSALS. A green "reuse" lets a queue entry
merge without building, so every way a candidate can be wrong has to give a full build:
a different workflow, a failed or still-running run, a fork, a stale result, a skipped
or missing job, an expired artifact, a malformed tree, and any API error at all. A
passing CI run would never show any of these as broken, so each one is a case here.

Run directly (`python3 test_merge_queue_reuse.py`); no test framework needed.
Exits 0 if every case behaves, 1 otherwise.
"""

from __future__ import annotations

import contextlib
import copy
import io
import os
import sys
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import merge_queue_reuse as mqr  # noqa: E402

REPO = "daqifi/daqifi-app"
TREE = "a" * 40
NOW = datetime(2026, 11, 17, 12, 0, tzinfo=timezone.utc)
REQUIRED = ["CI script self-tests", "Library + Desktop head + capture harness",
            "Desktop head on macOS", "Android head", "iOS head",
            "Avalonia version lockstep"]
SKIP = "Merge-queue reuse check"
WORKFLOW = ".github/workflows/build.yml"


def good_world() -> dict:
    return {
        "artifacts": [{"id": 1, "name": mqr.ARTIFACT_PREFIX + TREE, "expired": False,
                       "created_at": "2026-11-17T11:00:00Z",
                       "workflow_run": {"id": 100}}],
        "runs": {100: {"path": WORKFLOW, "event": "pull_request", "status": "completed",
                       "conclusion": "success", "updated_at": "2026-11-17T11:00:00Z",
                       "head_repository": {"full_name": REPO}}},
        "jobs": {100: [{"name": n, "conclusion": "success"} for n in REQUIRED]
                 + [{"name": SKIP, "conclusion": "skipped"},
                    {"name": "Record the green tree", "conclusion": "success"}]},
    }


def make_api(world: dict):
    calls: list[str] = []

    def api(path: str):
        calls.append(path)
        if path.startswith(f"/repos/{REPO}/actions/artifacts?"):
            return {"artifacts": world["artifacts"]}
        if "/jobs?" in path:
            run_id = int(path.split("/runs/")[1].split("/")[0])
            return {"jobs": world["jobs"][run_id]}
        run_id = int(path.rsplit("/", 1)[1])
        run = world["runs"][run_id]
        return run() if callable(run) else run
    api.calls = calls
    return api


def decide(world: dict, tree: str = TREE, required=None, sleeps=None):
    return mqr.decide(make_api(world), REPO, tree, workflow_path=WORKFLOW,
                      required=REQUIRED if required is None else required,
                      skip_job=SKIP, now=NOW, max_age=timedelta(hours=24),
                      sleep=(sleeps.append if sleeps is not None else lambda s: None))


def main() -> int:
    failures: list[str] = []

    def check(label: str, expected_reuse: bool, result, must_mention: str = "") -> None:
        reuse, reason = result
        ok = reuse == expected_reuse and must_mention in reason
        print(f"  [{'ok' if ok else 'FAILED'}] {label}: reuse={reuse} ({reason})")
        if not ok:
            failures.append(label)

    check("a verified green PR run is reused", True, decide(good_world()), "run 100")

    w = good_world(); w["artifacts"] = []
    check("no artifact for this tree", False, decide(w), "no green pull-request run")

    check("malformed tree", False, decide(good_world(), tree="HEAD"), "40-hex")
    check("tree with a query-string payload", False,
          decide(good_world(), tree=TREE + "&name=x"), "40-hex")
    check("no required jobs named", False, decide(good_world(), required=[]),
          "no required jobs")

    w = good_world(); w["runs"][100]["path"] = ".github/workflows/other.yml"
    check("a different workflow", False, decide(w), "not '.github/workflows/build.yml'")

    w = good_world(); w["runs"][100]["event"] = "merge_group"
    check("a merge_group run (never vouches)", False, decide(w), "not pull_request")

    w = good_world(); w["runs"][100]["conclusion"] = "failure"
    check("a failed run", False, decide(w), "completed/failure")

    w = good_world(); w["runs"][100]["head_repository"] = {"full_name": "evil/fork"}
    check("a fork's run", False, decide(w), "evil/fork")

    w = good_world(); w["runs"][100]["head_repository"] = None
    check("a run with no head repository", False, decide(w), "None")

    w = good_world(); w["runs"][100]["updated_at"] = "2026-11-16T11:00:00Z"
    check("a result older than 24 h", False, decide(w), "older than")

    w = good_world(); w["artifacts"][0]["expired"] = True
    check("an expired artifact", False, decide(w), "expired")

    w = good_world(); w["artifacts"][0]["workflow_run"] = {}
    check("an artifact that names no run", False, decide(w), "names no workflow run")

    w = good_world(); w["jobs"][100] = [j for j in w["jobs"][100] if j["name"] != "iOS head"]
    check("a required job missing from the run", False, decide(w), "'iOS head'")

    w = good_world()
    for j in w["jobs"][100]:
        if j["name"] == "Desktop head on macOS":
            j["conclusion"] = "skipped"
    check("a required job that was skipped", False, decide(w), "'Desktop head on macOS'")

    w = good_world(); w["jobs"][100].append({"name": "Some new job", "conclusion": "skipped"})
    check("an unlisted job that was skipped", False, decide(w), "'Some new job'")

    w = good_world()
    for j in w["jobs"][100]:
        if j["name"] == SKIP:
            j["conclusion"] = "success"
    check("the reuse check itself ran in the PR run", False, decide(w), SKIP)

    w = good_world(); w["jobs"][100].append({"name": "Android head", "conclusion": "success"})
    check("a required job reported twice", False, decide(w), "'Android head'")

    # Newest first, and a bad newest candidate does not hide a good older one.
    w = good_world()
    bad = copy.deepcopy(w["runs"][100]); bad["conclusion"] = "failure"
    w["runs"][200] = bad
    w["artifacts"].insert(0, {"id": 2, "name": mqr.ARTIFACT_PREFIX + TREE,
                              "expired": False, "created_at": "2026-11-17T11:30:00Z",
                              "workflow_run": {"id": 200}})
    check("a failed newer candidate, a good older one", True, decide(w), "run 100")

    # An artifact list that ignores the name filter must not leak a different tree.
    w = good_world(); w["artifacts"][0]["name"] = mqr.ARTIFACT_PREFIX + "b" * 40
    check("an artifact for a different tree", False, decide(w), "no green")

    # A still-running candidate is waited for, then accepted once it completes.
    w = good_world(); states = iter(["in_progress", "in_progress", "completed"])
    base = w["runs"][100]
    w["runs"][100] = lambda: dict(base, status=next(states))
    sleeps: list[float] = []
    check("a run that completes while waited for", True, decide(w, sleeps=sleeps), "run 100")
    check("  ...after exactly two waits", True, (sleeps == [mqr.WAIT_SECONDS] * 2, str(sleeps)))

    # ...and refused, after a bounded wait, if it never completes.
    w = good_world(); base = w["runs"][100]
    w["runs"][100] = lambda: dict(base, status="in_progress", conclusion=None)
    sleeps = []
    check("a run that never completes", False, decide(w, sleeps=sleeps), "in_progress")
    check("  ...after a bounded wait", True,
          (len(sleeps) == mqr.WAIT_POLLS, f"{len(sleeps)} waits"))

    # main(): any exception is a full build, and the exit code is always 0.
    def exploding(path):
        raise OSError("HTTP 502")
    args = ["--repo", REPO, "--tree", TREE, "--workflow-path", WORKFLOW,
            "--skip-job", SKIP] + sum((["--required", r] for r in REQUIRED), [])
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        code = mqr.main(args, api=exploding, now=NOW, sleep=lambda s: None)
    check("an API error gives a full build and exit 0", True,
          (code == 0 and "reuse=false" in out.getvalue() and "HTTP 502" in out.getvalue(),
           out.getvalue().strip().replace("\n", " | ")))

    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        code = mqr.main(args, api=make_api(good_world()), now=NOW, sleep=lambda s: None)
    check("main() reports a verified reuse", True,
          (code == 0 and "reuse=true" in out.getvalue(), out.getvalue().strip()))

    saved = os.environ.pop("GH_TOKEN", None)
    out = io.StringIO()
    try:
        with contextlib.redirect_stdout(out):
            code = mqr.main(args)
    finally:
        if saved is not None:
            os.environ["GH_TOKEN"] = saved
    check("no token gives a full build", True,
          (code == 0 and "reuse=false" in out.getvalue(), out.getvalue().strip()))

    if failures:
        print(f"\n{len(failures)} case(s) FAILED: {', '.join(failures)}")
        return 1
    print("\nall cases ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
