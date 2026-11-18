# Merge queue

The point of a merge queue is the failure that no amount of per-PR CI can catch: two
pull requests that each pass against `main` and break once both are in. Instead of
merging a PR's own (already-stale) head, GitHub builds a **merge group** — `main` plus
the queued PRs in order — and merges only if that tentative result is green. It also
means you can queue a stack of PRs and walk away, rather than babysitting
rebase-and-wait for each one.

**Status: live.** The ruleset on `main` is active and every merge goes through the
queue. See [How it is configured](#how-it-is-configured) for the settings actually in
force, and [What it cost](#what-it-cost) for the one workflow change that depended on
it.

## What is already done

`.github/workflows/build.yml` triggers on `merge_group`. That trigger is not optional:
a queue entry is built on a synthetic `refs/heads/gh-readonly-queue/main/pr-N-<sha>`
ref, and **a required check that lacks a `merge_group` trigger never reports on that
ref**. The entry does not fail fast — it sits until the queue's status-check timeout
expires and is then ejected, which looks like a broken queue rather than a missing
line of YAML.

Two properties of `build.yml` make it safe to build this way, and both are worth
preserving:

- **No job reads pull-request context.** Nothing references
  `github.event.pull_request`, `github.head_ref`, or `github.base_ref`, so every job
  behaves identically on a merge group. A new step that needs any of them must be
  gated on `github.event_name`.
- **`cancel-in-progress` is keyed on `pull_request` only.** A cancelled `merge_group`
  run reports as a *failed* required check, which ejects that PR from the queue and
  forces everything behind it to rebuild. Never broaden that condition to `true`.
- **There is no `push: branches: [main]` trigger, and that is load-bearing on the
  queue.** The `merge_group` run builds main-plus-one-PR squashed, which is exactly the
  commit that lands, so a push-triggered run rebuilt an already-built tree. It was
  removed. This is only safe while nothing can reach `main` outside the queue — see the
  `bypass_actors` note below. If that ever changes, the trigger must come back.

## Checks that are safe to require

Require only checks that run on `merge_group`. From `build.yml`:

| Check name | |
| --- | --- |
| `CI script self-tests` | guards' own tests |
| `Library + Desktop head + capture harness` | |
| `Desktop head on macOS` | macOS runner |
| `Android head` | |
| `iOS head` | macOS runner |
| `Avalonia version lockstep` | needs the three head jobs |

Two more jobs in `build.yml` are **not** to be required, and neither needs to be:
`Merge-queue reuse check` runs on `merge_group` only, and `Record the green tree` runs on
`pull_request` only. Both report as skipped (a pass) on the other event. See below.

## Reusing a green PR run of the same tree

A queue entry builds `main` plus one PR, squashed. When `main` has not moved since that
PR's last green run, the entry is byte-for-byte the tree the PR run already built. Over
2026-11-07..16 that was **18 of 53 queue entries (34%)**. Each one was a full rebuild,
with two macOS jobs and about 4 minutes added to the time to merge.

So on `merge_group`, the `Merge-queue reuse check` job looks for a green PR run of the
entry's exact git tree. If it finds and verifies one, it skips the four heavy jobs. The
required checks still report: a job skipped by its `if:` counts as Success. The
mechanism:

- A PR run in which **every** job passed uploads an artifact `green-tree-<tree>`, kept 1
  day. That is the `Record the green tree` job. `<tree>` is the tree of the commit that
  run checked out, which on `pull_request` is the test merge with `main`.
- `.github/scripts/merge_queue_reuse.py` accepts a candidate only if all of these hold:
  it is the same workflow file; the event is `pull_request`; the run is
  completed/success; it ran from a branch of this repository (never a fork, because a
  fork's run executes the fork's workflow and could name an artifact after someone
  else's tree); it finished within 24 h; and every job succeeded, other than the reuse
  check itself. Its self-test (`test_merge_queue_reuse.py`, run by `CI script
  self-tests`) has one case per refusal.
- **Every failure mode gives a full build.** That covers a script error, an API error, a
  timeout and a missing token. The heavy jobs use `!cancelled() && reuse != 'true'`,
  not the implicit `success()`. With `success()`, a failed reuse check would skip them,
  and skipped required checks pass, so the queue would merge an unbuilt tree. Keep
  that condition as it is.
- **Kill switch:** set the repository variable `CI_QUEUE_REUSE` to `off`.

If you add a required check, add it to the `--required` list in the `queue-reuse` job
too. The script already refuses any run in which some job did not succeed, so a missed
entry costs nothing in safety. The list makes the check say which job was missing.

**Do not require `Claimed bumps are actually applied`** (`dependabot-claims-check.yml`).
It verifies a Dependabot PR's description against its diff, and a merge group has no
single PR body to verify — it cannot be made merge-queue-aware, so it stays a
pull-request-time check. Requiring it would hang every queue entry until timeout. The
same reasoning applies to any future check whose input is the PR itself rather than
the resulting tree.

## How it is configured

Settled in **Settings → Rules → Rulesets** on `main` (ruleset `main`, enforcement
`active`). The settings in force, which are the ones the notes above reason about:

| Setting | Value |
| --- | --- |
| Merge method | `SQUASH` |
| Max entries to build concurrently | `2` |
| Min / max entries to merge | `1` / `1` |
| Min wait before merging | 5 minutes |
| Grouping strategy | `ALLGREEN` |
| Status-check timeout | 60 minutes |
| Required checks | the six in the table above |
| `bypass_actors` | **empty** |

Two of those rows are relied on elsewhere and should not be changed casually:

- **`max_entries_to_merge: 1`** keeps a queue entry equal to one PR. Batching merges a
  group as one unit, so one bad PR fails the whole batch and everything re-forms. It is
  also what makes the entry's build identical to the commit that lands, which is the
  premise of dropping the `push` trigger.
- **`bypass_actors: []`** is what makes `main` fully covered without a `push` trigger.
  Adding a bypass actor — an admin, an app, a deploy key — reopens the hole that trigger
  used to close. Add the trigger back in the same change, or do not add the actor.

Alongside the ruleset, the repository also forbids `non_fast_forward` on `main` and
requires a pull request (0 approvals, squash only).

## What it cost

Before the queue existed, every merge produced three full runs of `build.yml`: the PR's
own, then — once the queue arrived — the `merge_group` entry, then a `push` run on
`main`. Measured over one week: 58 `pull_request`, 13 `merge_group`, 13 `push`. The last
two are perfectly paired because they are the same tree.

That mattered because `build.yml` has two `macos-latest` jobs (`Desktop head on macOS`,
`iOS head`) and macOS runners bill at **10×** on private repositories, so each redundant
run was roughly 10 minutes of wall clock at 10× — about a third of this workflow's spend
for no new information. Dropping the `push` trigger removed it.

The queue still rebuilds *per entry*, so N queued PRs cost roughly N full builds. That
is why concurrency is 2 rather than the default 5; if the repository ever goes public,
Actions minutes become free and this stops mattering.
