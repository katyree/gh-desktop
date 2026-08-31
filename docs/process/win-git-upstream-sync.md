# WinGit upstream baseline and sync policy

Last verified: 2026-08-31

This document defines where WinGit came from and how to bring changes from
GitHub Desktop into the fork without mixing them with WinGit product work.

## Recorded baseline

WinGit starts from GitHub Desktop's `development` branch at this commit:

```text
b17e06dd0f0d9a45807eb39a51d223f52eb14da9
```

The commit is `Merge pull request #21535 from
jackfreem/fix/issue-21392-image-diff-alignment`, committed on
2026-08-20 at 17:11:26 UTC.

At the time this baseline was recorded, the local `development` branch,
`origin/development`, and `upstream/development` all pointed to that commit.
Their divergence was `0 0`.

## Repository ownership

The remote names and URLs are part of the project policy. They must not depend
on a contributor's existing Git configuration.

| Remote | Purpose | Required URL |
| --- | --- | --- |
| `origin` | WinGit fork owned by this project | `https://github.com/katyree/gh-desktop.git` |
| `upstream` | Canonical GitHub Desktop source | `https://github.com/desktop/desktop.git` |

If either remote is missing or incorrect, repair it before starting product
work:

```powershell
git remote set-url origin https://github.com/katyree/gh-desktop.git
git remote get-url upstream 2>$null
if ($LASTEXITCODE -eq 0) {
  git remote set-url upstream https://github.com/desktop/desktop.git
} else {
  git remote add upstream https://github.com/desktop/desktop.git
}
```

## Fetch schedule

Fetch both remotes at these points:

- Before starting each numbered WinGit issue.
- At least once a week while the fork is under active development.
- Immediately before a preview, release, or upstream-sync rehearsal.

Fetching updates remote-tracking refs. It does not merge upstream changes into
the current branch.

```powershell
git fetch --prune origin
git fetch --prune upstream
```

Record the upstream commit observed at the start of an issue in that issue or
pull request. If it differs from WinGit's current integration branch, report
the divergence; do not silently add an upstream merge to feature work.

```powershell
git rev-parse origin/development
git rev-parse upstream/development
git rev-list --left-right --count origin/development...upstream/development
```

The two counts are commits unique to `origin/development` and commits unique to
`upstream/development`, in that order.

## Upstream sync procedure

An upstream sync is its own issue and pull request. Perform it in an isolated
worktree so uncommitted product work in another checkout remains untouched.
The following commands create a sync branch from the current WinGit integration
branch:

```powershell
git fetch --prune origin
git fetch --prune upstream
git worktree add ..\gh-desktop-upstream-sync -b codex/sync-upstream-YYYY-MM-DD origin/development
Set-Location ..\gh-desktop-upstream-sync
git merge --no-ff upstream/development
```

Replace `YYYY-MM-DD` with the sync date. If the worktree path or branch already
exists, choose a new explicit path or remove the obsolete worktree only after
confirming it contains no work that must be preserved.

Use a merge commit for the long-running fork. Do not rebase or force-push the
shared WinGit integration branch.

Keep an upstream sync mechanical:

- Do not add WinGit features or fixes.
- Do not broadly format, rename, reorder, or clean up unrelated code.
- Preserve upstream behavior unless it directly conflicts with an intentional,
  documented WinGit difference.
- Resolve only the files required to complete the merge.
- Put follow-up improvements in separate issues and pull requests.

## Conflict record

When the merge reports conflicts, add a **Conflict record** section to the sync
issue or pull request. Use one row per conflicted file:

| File | Upstream change | WinGit difference | Resolution | Verification |
| --- | --- | --- | --- | --- |
| `path/to/file` | What upstream changed | Why WinGit differs | Which behavior was retained | Narrow check performed |

Also record:

- The exact `origin/development` and `upstream/development` commit IDs.
- Any upstream behavior intentionally not adopted and its reason.
- Any follow-up issue needed because a mechanical resolution was insufficient.
- The commands and observed results used to verify the sync.

Never resolve a conflict by accepting an entire side without first reviewing
the affected WinGit behavior.

## Read-only baseline check

Run this check from the repository root to confirm the documented remotes and
baseline without changing branches or files:

```powershell
git remote get-url origin
git remote get-url upstream
git fetch --prune upstream
git rev-parse HEAD
git rev-parse origin/development
git rev-parse upstream/development
git rev-list --left-right --count HEAD...upstream/development
git merge-base HEAD upstream/development
git status --short --branch
```

For the recorded baseline, the first three commit commands and `merge-base`
return `b17e06dd0f0d9a45807eb39a51d223f52eb14da9`, and the divergence command
returns `0 0`. Later upstream movement is expected. When it occurs, retain this
historical baseline and record the newer commit in the relevant sync issue.

The final status command may show contributor work. A baseline check must not
reset, clean, stage, or otherwise modify it.
