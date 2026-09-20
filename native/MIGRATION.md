# Native migration inventory

WinGit keeps the Electron app under `app` as the behavior reference while the
native WinUI 3 app under `native` moves toward feature parity. Each row below
is a bounded work unit that can be implemented and reviewed independently.

`Verified` means that the whole row acceptance criterion was observed.
`Partial` means that native code, a focused Core test, or a capture covers only
part of the row. `Present` means that the native files exist. `Next` marks the
next intended slice. `Remaining` means that the native work still needs an
implementation. A Core API or a screenshot does not verify the complete UI
workflow in a row.

Dependencies use the work-unit numbers in this inventory. Paths in the source
column point to the existing Electron behavior or the native boundary that
the work must connect to.

## Current evidence

The current evidence records focused checks individually rather than relying
on a repository-wide test count. The focused undo and tag run passed all 3
tests:

```powershell
dotnet test .\native\WinGit.Core.Tests\WinGit.Core.Tests.csproj --no-restore --filter FullyQualifiedName~GitRepositoryUndoTagsTests
```

The 3 passing cases cover undoing a dirty commit, undoing an initial commit,
and listing an annotated tag with a guarded stale delete.

The current evidence includes these non-empty captures from native build output
or copied published trees:

- `native/artifacts/screenshots/changes-light-03.png` shows the Changes view,
  a repository path, changed files, and a text diff.
- `native/artifacts/screenshots/history-dark-02.png` shows the History view,
  commit metadata, committed files, and a selected diff.
- `native/artifacts/screenshots/history-comparison-published-dark.png` shows a
  diverged native branch comparison from the copied published tree, with both
  endpoint IDs and `Behind 1` and `Ahead 1` counts.
- `native/artifacts/screenshots/settings-light-01.png` shows native theme and
  recent-repository settings.
- `native/artifacts/screenshots/branches-dark-01.png` shows the native branch
  list and branch details.
- `native/artifacts/screenshots/worktrees-light-01.png` shows worktrees and
  stashes in the native repository view.
- `native/artifacts/screenshots/undo-tags-final-history-light.png` shows the
  current HEAD summary and ID with native undo and tag actions.
- `native/artifacts/screenshots/undo-tags-final-tags-light.png` shows the native local
  tags view and its empty state.
- `native/artifacts/screenshots/codex-settings-light-final.png` shows the native
  Codex account, usage, and model settings sections in the unavailable state.
- `native/artifacts/screenshots/github-accounts-final-settings-signedout-light.png`
  shows the native GitHub accounts section in its unconfigured signed-out state,
  with account actions disabled until a public client is configured.
- `native/artifacts/screenshots/resume-clone-success-light-20260905.jpg` shows
  the copied `9B98` artifact cloning the synthetic repository from
  `native/artifacts/native-workflow-20260905-173830` into
  `native/artifacts/native-clone-ui-20260905`. The clone dialog showed progress,
  then opened the clean `main` branch at `origin/main` with `In sync`. Independent
  Git inspection reported HEAD
  `892b61e81d6291391afa3501c48e861cb0ade0cc` and a clean status. This proves
  local transport only; remote authentication was not exercised.
- `native/artifacts/screenshots/merge-conflict-branches-dark-04.png` shows a
  real synthetic merge conflict in the native operation panel, including the
  current branch and HEAD, merge target, conflicted path, and recovery actions.
- `native/artifacts/screenshots/conflict-editor-light-final.png` shows the native
   per-file conflict editor with Ours and Theirs content, side-selection actions,
   and an editable resolved-text box for a synthetic text conflict.
- `native/artifacts/screenshots/commit-message-generation-fixed-staged-light.png`
  shows a real staged-file Changes view with the full commit composer, including
  the Commit and Amend controls, visible in the native viewport.
- The commit panel now exposes a native Codex generation action. It captures
  the exact raw staged patch and the complete index fingerprint. Amend mode
  compares the index with HEAD's first parent, or the empty tree for a root
  commit. The caller captures the root, HEAD, model slug, reasoning effort,
  amend choice, and draft before dialogs, then revalidates the snapshot before
  and after generation. No live generation was run during this read-only
  verification.

The focused `GitRepositoryCommitMessageSnapshotTests` run passed 1 test. It
covers staged-only input, rejects a same-status restage through the full index
fingerprint, verifies an amend whose replacement index is empty relative to
the previous commit, and checks the SHA-256 repository empty-tree baseline.

These files are local verification artifacts and are ignored by
`native/.gitignore`. The capture path was exercised with
`--capture <absolute-png> --view <changes|history|settings|branches|worktrees|stashes|remotes|tags>
--repository <path> --theme <light|dark>`. The captures prove native startup,
repository loading, and rendering for the captured views. They do not prove
full staging, history search, authentication, or accessibility behavior.

Rows 21 and 33 now have native UI and Core API coverage. `MainWindow.UndoTags.cs`
uses `UndoCommitAsync` with the expected HEAD guard and uses `GetTagsAsync`,
`CreateTagAsync`, and `DeleteTagAsync` with the tag object ID guard. The History
capture shows the undo target summary and short ID. The Tags capture shows the
native list, create action, details panel, and empty state. The `9B98` artifact
also confirmed the Undo and annotated tag create or delete writes on the
synthetic fixture. Initial-commit, alternate-worktree, broader refresh, and
newer-source-build checks remain open.

The `native/build.ps1` `win-x64` publish now completes. A copied published
tree in `native/artifacts/publish-fixed-root-check` launched successfully and
rendered `remotes-published-light-01.png`; the published root included
`App.xbf`, `MainWindow.xbf`, `WinGit.Native.pri`, and `Assets/icon-logo.ico`.
The earlier `PRI175` (`0x80070020`) attempt was a file-in-use failure and is
historical evidence, not the result of the current publish.

Rows 27 and 28 now have native operation-panel and Core API coverage. The
native panel reads merge and rebase state, captures branch tips and expected
HEADs before confirmation, lists conflict paths, and routes them to Changes;
it provides refresh, continue, skip for rebase, and abort actions with a
second state check before mutation. The per-file editor reads one immutable
conflict snapshot, keeps Ours/Theirs semantics clear during rebase, and sends
explicit Apply or Apply and stage requests through the Core stale-snapshot guard.
The synthetic captures prove rendered states only. Interactive recovery,
successful manual writes, in-app conflict suggestions, and the original
external-editor/reveal actions still need desktop verification or later native
workflow slices.

An earlier verification host denied direct desktop input through `GetCursorPos`,
so its captures used the app's `RenderTargetBitmap` path. Current bounded
Computer Use evidence opened the native folder picker, closed it with Escape,
and closed the native process. The picker host remains difficult to target with
mouse automation, and chooser selection plus the newer workflow slices remain
unverified. The native app still uses WinUI controls directly.

The `whole-file-staging-check` capture creates a fresh synthetic child under
the supplied fixture parent and drives the native selected-file and visible-row
staging handlers. The run at
`native/artifacts/task15-handler-check/whole-file-staging.png` recorded 57
passing assertions in the adjacent `.checks.txt` report. The assertions cover
the index contents, worktree bytes, filtered rows, and hidden unselected file.
The copied app's `WinGit.Native.dll` SHA-256 was
`6CED01C1126DF7D428C8802F22AE46ABAB12AF7CF48A451DD4792524C1E86022`.
Independent Git inspection found a clean index, three modified worktree files,
and the hidden file's initial index content alongside its changed worktree
content. The screenshot shows the accessible controls, and the diagnostic
invoked `RunFileMutationAsync` directly without physical clicks. Interactive
mouse verification remains unavailable because
`IGraphicsCaptureItemInterop.CreateForMonitor` returned `0x80070057`,
`set_value` reported `Requested property was not in CacheRequest
(0x80070057)`, and click coordinate input geometry was unavailable.

To reproduce the check, run the current copied app with a fresh fixture parent
and an isolated settings directory:

```powershell
$repoRoot = (Resolve-Path .).Path
$handlerParent = Join-Path $repoRoot ("native\artifacts\task15-handler-check-" + [guid]::NewGuid().ToString("N"))
$handlerProfile = Join-Path $handlerParent "profile"
$handlerCapture = Join-Path $handlerParent "whole-file-staging.png"
New-Item -ItemType Directory -Force -Path $handlerProfile | Out-Null
$env:WINGIT_NATIVE_SETTINGS_DIRECTORY = $handlerProfile
& (Join-Path (Resolve-Path ".\native\artifacts\task15-handler-app").Path "WinGit.Native.exe") `
  --capture $handlerCapture `
  --view whole-file-staging-check `
  --repository $handlerParent `
  --theme light
Remove-Item Env:WINGIT_NATIVE_SETTINGS_DIRECTORY
```

The `partial-staging-check` capture creates a fresh synthetic child under
the supplied fixture parent and drives the native partial line/hunk
selection and mutation handlers. The run at
`native/artifacts/task16-partial-check/partial-staging.png` recorded 42
passing assertions in the adjacent `.checks.txt` report. The fixture starts
with a staged `line 02` change in the target file, two unstaged added lines
in one hunk, a separate unstaged `line 30` hunk, one unrelated staged file,
and one untracked file. The assertions cover staging a single selected line
(the index keeps the preexisting staged change and gains only that line),
staging a whole hunk, unstaging a staged hunk while retaining the other
staged changes in the same file, and rejecting a stale selection after the
worktree changed with a `Refresh required` error while preserving the index
and worktree bytes. The focused `GitRepositoryPartialStagingTests` run
passed 5/5, covering line and whole-hunk staging with a preexisting
same-file staged change, a partial-unstage round trip, stale stage rejection
after worktree and index-only changes, stale unstage rejection after an
index change, and exact unrelated index and worktree preservation; the full
Core suite passed 120/120. The copied app's `WinGit.Native.dll` SHA-256 was
`47325606BE7701A8AB29EF4AC64AAC7D530BFA97E2EDB3ED63AEA6BB394D9BA0`.
Independent Git inspection found `MM partial-target.txt`,
`M unrelated-staged.txt`, and untracked `untracked file.txt`; the index
holds the preexisting staged line plus the selected addition only, while the
worktree keeps both additions and the stale edit. The screenshot shows the
rendered view, and the diagnostic invoked `ApplyPartialSelection` and
`RunPartialMutationAsync` directly without physical clicks. Interactive
mouse verification remains unavailable because
`IGraphicsCaptureItemInterop.CreateForMonitor` returned `0x80070057`,
`set_value` reported `Requested property was not in CacheRequest
(0x80070057)`, and click coordinate input geometry was unavailable.

To reproduce the check, run the current copied app with a fresh fixture parent
and an isolated settings directory:

```powershell
$repoRoot = (Resolve-Path .).Path
$handlerParent = Join-Path $repoRoot "native\artifacts\task16-partial-check"
$handlerProfile = Join-Path $handlerParent "profile"
$handlerCapture = Join-Path $handlerParent "partial-staging.png"
New-Item -ItemType Directory -Force -Path $handlerProfile | Out-Null
$env:WINGIT_NATIVE_SETTINGS_DIRECTORY = $handlerProfile
& (Join-Path (Resolve-Path ".\native\artifacts\task16-partial-app").Path "WinGit.Native.exe") `
  --capture $handlerCapture `
  --view partial-staging-check `
  --repository $handlerParent `
  --theme light
Remove-Item Env:WINGIT_NATIVE_SETTINGS_DIRECTORY
```

The `partial-discard-check` capture creates a fresh synthetic child under
the supplied fixture parent and drives the native partial line/hunk
selection and discard handlers. The run at
`native/artifacts/task20-partial-discard-check/partial-discard.png`
recorded 50 passing assertions in the adjacent `.checks.txt` report. The
fixture starts with a staged `line 02` change in the target file, two
unstaged added lines plus a deleted line in one hunk, a separate unstaged
`line 30` hunk, one unrelated staged file, one untracked file, one CRLF
file, one file without a trailing newline, and one NUL-byte binary file.
The assertions cover discarding a single selected line (the index keeps
the preexisting staged change and only that line leaves the worktree),
discarding a whole hunk (the deleted line is restored), an explicit
cancel path that changes nothing, confirmation content that names the
file with its line and hunk scope, rejecting a stale selection after the
worktree changed with a `Partial discard stopped; refresh required`
error while preserving the index and worktree bytes, exact CRLF and
final-newline byte round-trips, and a binary selection that reports
`Line selection unavailable` with the worktree bytes preserved and no
whole-file fallback. The focused `GitRepositoryPartialDiscardTests` run
passed 4/4, covering line and whole-hunk discard with a preexisting
same-file staged change, stale discard rejection, deletion-hunk discard
with unrelated index and worktree preservation, and binary-unsupported
rejection; the full Core suite passed 133/133. The copied app's
`WinGit.Native.dll` SHA-256 was
`284152AC14E2A6DD4AA4201674100028E0AE5AE648A461EF571428314EDFD6DA`.
Independent Git inspection found `M binary-target.bin`,
`MM partial-discard-target.txt`, `M  unrelated-staged.txt`, and untracked
`untracked file.txt`; the index holds only the preexisting staged line
while the worktree keeps the unselected `line 30 keep` edit with the
discarded lines gone. The screenshot shows the rendered Changes view
with the binary unavailable message, and the diagnostic invoked
`ApplyPartialSelection`, the confirmation-content builder, and
`DiscardSelectedChangesAsync` directly without physical clicks. The
`ContentDialog` confirmation itself was not clicked, and interactive
mouse verification was not attempted. Whole-file discard verification
stays with task 19; this row is not fully verified until both slices
have evidence.

To reproduce the check, copy the current published tree, then run the
copied app with a fresh fixture parent and an isolated settings
directory:

```powershell
$repoRoot = (Resolve-Path .).Path
$handlerParent = Join-Path $repoRoot ("native\artifacts\task20-partial-discard-check-" + [guid]::NewGuid().ToString("N"))
$handlerProfile = Join-Path $handlerParent "profile"
$handlerCapture = Join-Path $handlerParent "partial-discard.png"
$handlerApp = Join-Path $repoRoot "native\artifacts\task20-partial-discard-app"
New-Item -ItemType Directory -Force -Path $handlerProfile | Out-Null
Copy-Item -Recurse -Path (Join-Path $repoRoot "native\artifacts\win-x64") -Destination $handlerApp
$env:WINGIT_NATIVE_SETTINGS_DIRECTORY = $handlerProfile
& (Join-Path $handlerApp "WinGit.Native.exe") `
  --capture $handlerCapture `
  --view partial-discard-check `
  --repository $handlerParent `
  --theme light
Remove-Item Env:WINGIT_NATIVE_SETTINGS_DIRECTORY
```

The native appearance scope is one light palette and one dark palette. The
`System` setting chooses between those two palettes. User-defined theme
palettes are outside this migration target.

Work unit 39 is `Partial`. The Core client now lists and reads repository-bound
pull requests, changed files, and review metadata with bounded partial results.
The native Remotes workspace source adds host-matched account selection,
plain-text Overview/Files/Reviews tabs, safe Open on GitHub validation, and
loading, empty, error, and cancellation states with tracked cleanup. Live
account/network and desktop capture verification remain unavailable; merge
status, checks, creation, review submission, checkout, and merge writes remain.

Work unit 14 is `Partial`. `native/WinGit.Core/Models.cs` now carries optional
`FileDiff.ImageComparison` payloads with defensive raw bytes, and
`GitRepositoryImageDiff.cs` enriches working, index, unstaged, commit, and
history-selection reads with bounded PNG, JPEG, GIF, BMP, TIFF, ICO, and WebP
content. The focused image test passed 1/1; the related service and selection
checks passed 11/11 and 2/2, and the Core build reported zero warnings and
errors. The copied `84A6` artifact opened the synthetic 240x160 fixture in
both themes, showed unstaged blue-to-green and staged orange-to-blue image
comparisons, showed an added image with no previous side, and cleared the
image view for opaque binary content. See
`native/artifacts/resume-verification-note-20260905.md` for the artifact hash,
source details, and screenshots. The 65C1 copied artifact also showed a valid
PNG before side paired with `Preview unavailable: This revision is not a
supported image.` for an unsupported after side, without deleted-file wording;
see `native/artifacts/screenshots/native-65c1-image-unsupported-after-light-20260905.jpg`.
The `A9F1` published artifact and copy completed with exit code 0, zero build
errors, and one `NU1900` audit-service warning. Its Light desktop pass showed
Swipe at 50% and at 100%, Onion Skin at 100% and 49%, Difference output,
opaque-binary image clearing, and a History added-image fallback. The later
`12A1` copy persisted Swipe across restart, named the active mode in the
status text, and kept relative image sizes when switching from Difference to
Swipe. A Dark pass covered Swipe at 50%, Onion Skin at 50%, and Difference on
the same unequal-size fixture. See
`native/artifacts/resume-verification-note-20260905.md` and the
`native-a9f1-*` and `native-12a1-*` screenshots listed there. Broader Dark
coverage outside that fixture, full Electron parity, SVG or DDS behavior, and
external binary opening remain unverified. Submodule state, stale-target
handling, update, dirty guarding, and nested text-diff opening are partially
covered under work unit 50.

## 1. Platform, launch, and app shell

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 1 | Keep the native WinUI 3 shell in `native/WinGit.Native`, including `App.xaml`, the x64 manifest, the production icon, and `native/build.ps1`. | None | Partial | The project restores, builds, and publishes with the `win-x64` runtime without changing the Electron app. |
| 2 | Launch the native window and route an optional repository path from `native/WinGit.Native/App.xaml.cs`; use `app/src/ui/repository.tsx` as the reference. | 1 | Partial | Starting the published executable opens a native window, and the first positional path reaches repository-open handling without reading Electron auth state. |
| 3 | Provide the native window frame and view navigation; use `app/src/ui/window` and `app/src/ui/repository.tsx` as the reference. | 2 | Partial | A user can move between the repository, changes, diff, and history views with native controls. |
| 4 | Provide the repository chooser and recent-repository list; use `app/src/ui/repositories-list`, `app/src/lib/stores/repositories-store.ts`, and `app/src/lib/databases/repositories-database.ts`. | 3 | Partial | Native source now builds the chooser from normalized recent paths, keeps unavailable entries until explicit removal, and opens a selected valid path. A copied artifact opened the folder picker and closed it with Escape. Chooser selection, missing-entry handling, and the current alias controls need the next serialized Native build and desktop pass. |
| 5 | Add an existing repository; use `app/src/ui/add-repository/add-existing-repository.tsx` and `app/src/lib/git/core.ts`. | 4, 10 | Partial | `OpenRepositoryAsync` validates the candidate status and operation state before committing the new context, so an invalid path or failed preload preserves the loaded repository. The folder picker open and close path is observed in a copied artifact. Successful add and actionable invalid-path UI need the next build and desktop pass. |
| 6 | Create a local repository with its optional README, ignore file, and license; use `app/src/ui/add-repository/create-repository.tsx`, `gitignores.ts`, and `app/src/lib/git/init.ts`. | 4, 46 | Partial | The new directory contains the requested Git initialization files and appears in the repository list. |
| 7 | Clone a GitHub or generic repository; use `app/src/ui/clone-repository` and `app/src/lib/git/clone.ts`. | 4, 46 | Partial | Native Clone shows progress and opens the resulting repository. The `9B98` artifact cloned a synthetic local source into `native/artifacts/native-clone-ui-20260905`, then opened clean `main` at `origin/main` with `In sync`; independent Git inspection confirmed a clean status and HEAD `892b61e81d6291391afa3501c48e861cb0ade0cc`. Remote authentication and transport-error handling remain unverified. |
| 8 | Implement the app menu, action dispatcher, and keyboard shortcuts; use `app/src/ui/app-menu`, `app/src/ui/dispatcher`, `app/src/lib/menu-item.ts`, and `app/src/ui/keyboard-shortcut`. | 3 | Remaining | Menu commands and their shortcuts invoke the same native actions, and failed actions reach one visible error path. |
| 9 | Handle application URLs, external editors, and shell commands; use `app/src/lib/parse-app-url.ts`, `app/src/ui/open-with-external-editor`, `app/src/ui/editor`, and `app/src/ui/shell`. | 3, 8 | Partial | Supported links open the intended repository or commit, an editor opens the requested file, and a shell opens at the repository root. Native has guarded repository and selected-file editor, shell, and File Explorer actions with discovered-tool persistence and repository-path containment. Application URL parsing, desktop launch proof, and full Electron parity remain unverified. |

## 2. Read-only repository state and diff

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 10 | Load and validate a repository through the C# core service; use `app/src/lib/git/core.ts`, `status.ts`, and `rev-parse.ts`. | 2, 3 | Partial | A valid repository returns a typed root context, and a non-repository returns a typed failure without changing files. |
| 11 | Show the current branch, detached or unborn state, upstream counts, and changed files; use `app/src/ui/repository.tsx`, `app/src/ui/changes`, `app/src/lib/git/status.ts`, `branch.ts`, and `rev-parse.ts`. | 10 | Partial | The native status view matches a temporary repository after clean, staged, unstaged, untracked, detached, and unborn states. |
| 12 | Render full and partial change selection plus filters; use `app/src/ui/changes/multiple-selection.tsx`, `filter-changes-list.tsx`, `filter-changes-logic.ts`, and `changes-list-filter-options.tsx`. | 11 | Partial | A user can select all, clear all, select individual files, and filter the list without changing the repository. |
| 13 | Render text diffs with mode, option, and search controls; use `app/src/ui/diff`, `app/src/ui/lib/diff-mode.tsx`, `app/src/lib/git/diff.ts`, and `app/src/lib/diff-parser.ts`. | 11, 12 | Partial | A selected file shows added, removed, context, hunk, and line-number data in the chosen diff mode. |
| 14 | Render image, binary, and submodule diffs; use `app/src/ui/diff/image-diffs`, `app/src/ui/diff/submodule-diff.tsx`, and `app/src/lib/git/submodule.ts`. | 13 | Partial | Each supported non-text change identifies its type and gives a useful native result without treating binary data as text. |
| 15 | Handle large and truncated diffs; use `app/src/ui/changes/oversized-files-warning.tsx`, `app/src/ui/diff/diff-contents-warning.tsx`, and `app/src/ui/diff/text-diff-expansion.ts`. | 13 | Remaining | Large content shows its limit and available action, and the view never freezes while loading a bounded diff. |
| 16 | Browse commit history and committed files; use `app/src/ui/history/commit-list.tsx`, `file-list.tsx`, `committed-file-item.tsx`, `app/src/lib/git/log.ts`, and `show.ts`. | 10, 11 | Partial | A repository with commits shows stable commit IDs, summaries, authors, dates, and the files in a selected commit. |
| 17 | Filter and compare history, then inspect selected commits; use `app/src/ui/history/compare.tsx`, `app/src/ui/history/commit-list.tsx`, and `app/src/lib/stores/app-store.ts`. | 16 | Partial | Native History filters local comparison branches, shows both captured endpoint IDs with Ahead and Behind counts, and uses immutable Core branch and commit-selection snapshots. Consecutive selections load a combined file list and diff in oldest-to-newest order. Nonconsecutive selections keep their metadata and explain why a combined diff is unavailable. The DC114 desktop pass selected `a3c4e7f` and `f60d80e`, matched the combined file list and independent Git diff, then selected root `5226466` while omitting `f06a5c6` and showed three selected metadata rows with the unavailable-diff explanation; see `native/artifacts/screenshots/native-dc114-history-consecutive-dark-20260905.jpg` and `native/artifacts/screenshots/native-dc114-history-nonconsecutive-dark-20260905.jpg`. The current-source A8C1 artifact deselected the only selected HEAD `b614078`, cleared central commit metadata and files, and showed `Select a commit to inspect its files.`; see `native/artifacts/screenshots/native-a8c1-history-empty-selection-dark-20260905.jpg`. Its selected empty commit remained in `Loading commit files`; the later 84A6 artifact showed `No files in this commit` and `No files changed in this commit`; see `native/artifacts/screenshots/native-84a6-empty-commit-footer-light-20260905.jpg`. Stale switching and full workflow parity still need verification. |

## 3. File staging and commit actions

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 18 | Stage and unstage full files and partial selections; use `app/src/lib/git/stage.ts`, `apply.ts`, `update-index.ts`, and `app/src/ui/changes`. | 12, 46 | Partial | Staging all, one file, and one selected hunk produces the expected index and leaves unselected changes in the worktree. |
| 19 | Commit selected files with message, author, and attribution controls; use `app/src/ui/changes/commit-message.tsx`, `app/src/ui/commit-message`, `app/src/lib/git/commit.ts`, and `app/src/ui/unknown-authors`. | 18 | Partial | A commit contains only the selected changes, preserves the chosen author, and displays Git errors without losing the message. The native composer now has co-author (`Name <email>`, one per line) and Signed-off-by controls backed by Core trailer validation and `git commit --signoff`; the commit path captures the draft and file selection up front, guards async results by operation generation and repository root, and restores the selection after a failed commit. The `commit-composer-check` handler run recorded 31 passing assertions covering the staged-only commit with co-author and sign-off trailers, author metadata, unstaged/untracked preservation, composer clearing on success, amend with attribution, stale-amend failure with draft and selection retention, and repository-switch draft isolation; the focused `GitRepositoryCommitComposerTests` run passed 3/3 and the full Core suite passed 130/130. Broader Electron parity (unknown-author lookup, per-repository author override, hook-specific failure UI) remains unverified. |
| 20 | Amend the previous commit; use `app/src/ui/changes/commit-message.tsx` and `app/src/lib/git/commit.ts`. | 19 | Partial | Native amend replaced fixture B HEAD `d17a49728a89a9fff8c06da727a72c38bbef3a3d` with `f60d80e8d3013faa69cd7070cb36f94052039810`, preserved the typed two-paragraph message, left the tree and status clean, and refreshed History; see `native/artifacts/screenshots/native-dc114-amend-success-light-20260905.jpg`. The current-source 51D3 artifact loaded the existing `f60d80e` subject/body into an empty Amend composer, restored a blank composer when Amend was toggled off, preserved an existing `Keep my existing draft` draft when toggled on, and rejected an amend after synthetic HEAD `b614078ddf6d721fa952ff643cf3dad1be1c6736` advanced while keeping the draft and clean Git state; see `native/artifacts/screenshots/native-51d3-amend-loaded-dark-20260905.jpg` and `native/artifacts/screenshots/native-51d3-amend-stale-head-preserves-draft-20260905.jpg`. The row remains `Partial` for broader unverified migration parity and edge paths. |
| 21 | Undo a commit; use `app/src/ui/changes/undo-commit.tsx`, `app/src/ui/undo`, and `app/src/lib/git/reset.ts`. | 19 | Partial | Native History exposes the current HEAD summary and ID, confirms branch and worktree effects, calls the Core expected-HEAD guard, restores the commit message after success, and refreshes Changes. On the `9B98` artifact, Undo showed the full `70029faf273f9ba7361c5c21dc2c41ef3ebd003e` target, returned HEAD to `a3c4e7f4bccc614642c0770e9b1b667bc63a38bc`, restored the composer, and preserved the untracked synthetic file; see `native/artifacts/screenshots/resume-undo-preserved-file-light-20260905.jpg`. Initial-commit, alternate worktree, and newer-source-build verification remain. |
| 22 | Discard full files and selected changes; use `app/src/ui/discard-changes` and `app/src/lib/git/rm.ts` and `reset.ts`. | 12, 46 | Partial | The confirmation names the affected paths, and the selected files or hunks disappear from the worktree only after confirmation. The selected-line/hunk slice is verified (partial-discard-check plus focused Core tests); whole-file discard verification stays with task 19. |
| 23 | Revert a commit; use `app/src/ui/toolbar/revert-progress.tsx`, `app/src/lib/git/revert.ts`, and `app/src/ui/history`. | 16, 46 | Partial | Native History captures the selected full commit ID, current branch, and expected HEAD before confirmation, calls the guarded Core revert, refreshes the repository, and exposes conflict paths with Continue, Skip, and Abort recovery. Continue now records an emptied revert with `commit --allow-empty` so it stays visible in history (matching the cherry-pick continue behavior), advancing the sequencer only when work remains. `GitRepositoryCherryPickRevertTests` covers the emptied-revert completion and the full Core suite passes 162/162. No native UI change was needed: the existing Continue action covers the outcome. Desktop confirmation and successful Git-write verification remain, so the row stays `Partial`. |
| 24 | Surface hooks, progress, retries, and local-change warnings; use `app/src/ui/commit-progress`, `app/src/ui/hook-failed`, `app/src/ui/local-changes-overwritten`, `app/src/ui/dispatcher/error-handlers.ts`, and `app/src/lib/hooks`. | 18, 19 | Remaining | A slow or failed Git action shows progress, preserves actionable stderr, and gives the user a safe retry or recovery action. |

## 4. Branch and commit history operations

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 25 | Create, select, rename, and delete branches; use `app/src/ui/branches`, `create-branch`, `rename-branch`, `delete-branch`, and `app/src/lib/git/branch.ts`. | 11 | Partial | Creation: the native dialog identifies the repository path, proposed name, and resolved starting commit, validates names through the existing `check-ref-format` boundary, rejects existing names, resolves the start point to an immutable commit, refuses a missing or changed start ref inside the serialized mutation, refreshes the branch list with the new branch selected, and offers an explicit switch-after-create that reuses the existing checkout guards (unsafe switches keep Git's protection; a failed switch reports the created-branch partial outcome). Rename/delete: the Branches view captures the selected branch tip and repository root before rename/delete dialogs, revalidates repository, selection, and tip after confirmation, and refreshes the list with the renamed branch selected. Core rename validates names, rejects exact collisions (a case-only spelling change uses `-M` like Electron), refuses missing and stale branches, and refuses branches checked out in another worktree. Core delete always uses safe `-d` (never a silent `-D`), refuses the current branch, linked-worktree checkouts, and the known remote-HEAD default branch, and reports missing and stale branches clearly; hosting-service protection data is never assumed when unavailable. The delete confirmation names the exact local branch, explains safe-delete unmerged-commit consequences, and states that no remote branches are touched. Broad checkout/worktree restrictions remain with task 25. |
| 26 | Check out branches and worktree targets with local-change warnings; use `app/src/ui/checkout`, `app/src/lib/git/checkout.ts`, and `app/src/ui/local-changes-overwritten`. | 25, 22 | Partial | The current-source 51D3 artifact exercised fixture F from `main` `c43eb2097a389b37fcba529be840e7931914e0f5` and `feature` `559fa9b69b14194a655a54d896a4220d7fd60a26`. Changing the feature target while the dialog was open produced a stale-target rejection while preserving main notes without a stash; restoring the target and choosing Bring changes succeeded with the feature changes and untracked `notes.txt` without a stash. Save stash and switch back to main left main clean, exposed `stash@{0}: On feature: WinGit: changes saved before switching to main`, and preserved the exact synthetic `notes.txt` content through `git show stash^3:notes.txt`. Apply selected stash then named `stash@{0}` short ID `19c974d`, restored the exact fixture-F notes, left main at `c43eb2097a389b37fcba529be840e7931914e0f5` with untracked `notes.txt`, and retained stash SHA `19c974da3d6a1ea43d82c57efd054235cfcd2371`; see `native/artifacts/screenshots/native-51d3-checkout-strategies-dark-20260905.jpg`, `native/artifacts/screenshots/native-51d3-checkout-stale-target-dark-20260905.jpg`, `native/artifacts/screenshots/native-51d3-checkout-bring-success-dark-20260905.jpg`, `native/artifacts/screenshots/native-51d3-checkout-stash-success-dark-20260905.jpg`, and `native/artifacts/screenshots/native-51d3-checkout-stash-restored-dark-20260905.jpg`. The filtered WorktreeStash plus service run had 12 passes and one failure on the conflict-hint assertion `Index was not unstashed`; the chooser's follow-up unmerged-status regression passed 1/1 with analyzers and package auditing enabled. The row remains `Partial` because broader checkout and worktree parity is not established. Checkout now refuses a target branch that is checked out in another linked worktree before any state change (no stash is created) across the plain, stash, and bring-changes strategies, naming the branch and worktree path; the Branches view pre-screens the switch the same way rename/delete do, with the Core guard authoritative for stale data. `GitRepositoryBranchesTests` covers all three refusal paths plus the current-branch no-op, and the full Core suite passes 154/154. A copied published artifact rendered the Branches view on a synthetic linked-worktree fixture (`linked` shown as `Checked out in a worktree`) without mutating it; interactive switch-click verification remains open. |
| 27 | Merge branches and recover from conflicts; use `app/src/ui/multi-commit-operation/merge.tsx`, `app/src/ui/merge-conflicts`, `app/src/lib/git/merge.ts`, and `merge-tree.ts`. | 25, 26 | Partial | Merge reports a clean result or lists unmerged files, and the user can continue, abort, or inspect conflict suggestions. Native squash merge is now implemented: `SquashMergeBranchAsync` stages the combined change without moving HEAD (refusing stale HEAD, other operations, and an already-staged squash), `ContinueSquashMergeAsync` commits it, and `AbortSquashMergeAsync` hard-resets to the revalidated pre-merge tip, verified to clear SQUASH_MSG. The merge dialog offers a squash option and the operation panel synthesizes a SquashMerge kind with Continue/Abort recovery. `GitRepositoryMergeTests` passes 10/10 and the full Core suite 158/158. A copied published artifact rendered the conflicted-squash panel (`Squash merge in progress`, staged summary, conflicted path, disabled Continue, Abort) without mutating the fixture. Interactive Continue/Abort clicks and in-app conflict suggestions remain unverified, so the row stays `Partial`. |
| 28 | Rebase branches and recover from conflicts; use `app/src/ui/multi-commit-operation/rebase.tsx`, `base-rebase.tsx`, `app/src/ui/rebase`, and `app/src/lib/git/rebase.ts`. | 25, 26 | Partial | Rebase reports progress, allows continue or abort, and leaves the repository in a known state after a conflict. Continue now skips a pick emptied by its resolution automatically (matching Electron's continue-with-empty-commit skip) instead of failing on `rebase --continue`; explicit skip shares the same guarded mutation path with no behavior change. `GitRepositoryRebaseTests` covers the auto-skip (conflict resolved to the base version continues as `Skipped`, HEAD at the base tip, clean status) and the full Core suite passes 159/159. No native UI change was needed: the existing Continue action and `Skipped` result text already cover the outcome. Interactive desktop verification of the full rebase workflow remains open, so the row stays `Partial`. |
| 29 | Cherry-pick commits and recover from conflicts; use `app/src/ui/multi-commit-operation/cherry-pick.tsx`, related banners, and `app/src/lib/git/cherry-pick.ts`. | 16, 25 | Partial | Native History confirms the current branch, expected HEAD, and immutable selected commit IDs, submits multiple commits oldest-to-newest through the Core stdin path, refreshes after the result, and exposes conflict paths with Continue, Skip, and Abort recovery. Continue now records an emptied pick with `commit --allow-empty` so it stays visible in history (matching Electron), advancing the sequencer only when picks remain; a final pick finishes on commit alone. `GitRepositoryCherryPickRevertTests` covers single-pick empty completion and multi-pick empty-then-applied sequencing, and the full Core suite passes 161/161. No native UI change was needed: the existing Continue action covers the outcome. Desktop confirmation and full progress/banner parity remain unverified, so the row stays `Partial`. |
| 30 | Reorder commits; use `app/src/ui/multi-commit-operation/reorder.tsx` and `app/src/lib/git/reorder.ts`. | 16, 25 | Partial | `CaptureReorderPlanFromCurrentHistoryAsync` derives the smallest replay base from current first-parent ancestry, while `CaptureReorderPlanAsync` preserves selected full IDs in chronological order, supports before-target, end, and root ranges, and rejects duplicate, missing, foreign, merge, dirty, and stale plans. `ReorderAsync` replays an owned temporary todo through interactive rebase, clears inherited `GIT_SEQUENCE_EDITOR`, and reports conflicts for existing recovery. `GitRepositoryReorderTests` passes 4/4, including inherited editor handling, current-ancestry range selection that retains an older merge outside replay, and unchanged `.git/config`. On the `9B98` artifact, native confirmation and successful reorder moved `b0dbe2a`, left the synthetic tree and Git status clean, and produced sequence `a3c4e7f`, `f06a5c6`, `5226466`; the fixture was confirmed 2 ahead and 2 behind its upstream. Plan capture now also refuses an unchanged replay order (`NoChange`) instead of rewriting every commit ID through a pointless interactive rebase; the existing preview dialog surfaces the refusal through its `Reorder unavailable` path with no UI change. `GitRepositoryReorderTests` passes 5/5 and the full Core suite 163/163. Current-source rebuild and broader conflict verification remain, so the row stays `Partial`. |
| 31 | Squash commits; use `app/src/ui/multi-commit-operation/squash.tsx`, `app/src/ui/banners/successful-squash.tsx`, and `app/src/lib/git/squash.ts`. | 16, 30 | Partial | Native History and Core source now capture selected full IDs, confirm the rewrite target, guard stale state, run the squash, and refresh the repository view. The Core squash tests cover the mutation and failure paths, including CR-only recovery normalization. The copied `native-line-endings-20260905` artifact verified native confirmation and a successful synthetic write: the exact title and body, clean status, and empty prior-HEAD diff were checked after the rewrite. The same artifact also verified conflict persistence across restart, conflict-editor resolution, Continue, a later conflict, and Skip; see `native/artifacts/screenshots/native-dc114-squash-preview-light-20260905.jpg`, `native/artifacts/screenshots/native-dc114-squash-success-light-20260905.jpg`, `native/artifacts/screenshots/native-dc114-squash-conflict-before-restart-20260905.jpg`, `native/artifacts/screenshots/native-dc114-squash-conflict-after-restart-20260905.jpg`, and `native/artifacts/screenshots/native-dc114-squash-recovery-success-20260905.jpg`. Successful squashes now arm a one-shot History undo (Electron `SuccessfulSquash` banner parity): `UndoSquashAsync` hard-resets to the plan's pre-squash tip only with a clean tree on the squashed branch, HEAD still at the post-squash tip, and no other operation active, refusing moved-HEAD reuse with `UndoUnavailable`. The success status names the squashed count and the undo offer; the Core squash suite covers undo restore plus moved-HEAD, dirty-tree, and reuse refusals (7/7) and the full Core suite passes 165/165. A copied published artifact rendered History with the new code without mutating the fixture. Interactive squash/undo clicks remain unverified, so the row stays `Partial`. |
| 32 | Reset a branch and inspect reflog or unreachable commits; use `app/src/ui/reset`, `app/src/ui/history/unreachable-commits-dialog.tsx`, `app/src/lib/git/reset.ts`, and `reflog.ts`. | 16, 25 | Partial | Native History captures a full selected commit ID, offers Soft/Mixed/Hard reset with branch/HEAD/target confirmation and Core expected-HEAD protection, and shows a bounded HEAD reflog with explicit Mixed recovery. Unreachable-commit browsing and desktop mutation confirmation remain unverified. |
| 33 | Create and delete tags; use `app/src/ui/create-tag`, `app/src/ui/delete-tag`, and `app/src/lib/git/tag.ts`. | 25 | Partial | Native Tags lists local tags, creates an annotated tag at current HEAD or a selected History commit, confirms deletion by name and target, uses the Core object-ID guard, and refreshes the list. On the `9B98` artifact, `native-verification-tag` was created at the selected target and deleted again without changing HEAD. The current-source rebuild and broader tag-list refresh verification remain. |

## 5. Remotes, transport, and GitHub workflows

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 34 | Manage remotes and fork contribution targets; use `app/src/ui/repository-settings/remote.tsx`, `fork-settings.tsx`, and `app/src/lib/git/remote.ts`. | 11, 46 | Remaining | A user can view, add, edit, and remove remotes, and the selected fork target is visible before push. |
| 35 | Publish a local repository; use `app/src/ui/publish-repository` and `app/src/lib/git/init.ts` and `remote.ts`. | 6, 34, 46 | Remaining | Publishing creates or selects the remote, pushes the initial branch, and shows the resulting remote state. |
| 36 | Fetch remotes; use `app/src/ui/toolbar/push-pull-button.tsx` and `app/src/lib/git/fetch.ts`. | 34, 46 | Remaining | Fetch updates remote refs, reports progress and authentication errors, and leaves local worktree files unchanged. |
| 37 | Pull changes and handle pull-required or merge states; use `app/src/lib/git/pull.ts`, `app/src/ui/push-needs-pull`, and `app/src/ui/multi-commit-operation`. | 34, 36, 46 | Remaining | Pull fast-forwards or reports the required merge or rebase path, with no silent overwrite of local changes. |
| 38 | Push changes and handle rejection or force-push guards; use `app/src/lib/git/push.ts`, `app/src/ui/workflow-push-rejected`, `app/src/ui/push-needs-pull`, and `app/src/ui/multi-commit-operation/dialog/warn-force-push-dialog.tsx`. | 34, 36, 46 | Remaining | Push reports success or the rejected ref, and force push requires a clear confirmation that names the affected branch. |
| 39 | List, open, and inspect pull requests and merge status; use `app/src/ui/branches/pull-request-list.tsx`, `app/src/ui/open-pull-request`, `app/src/ui/pull-request-quick-view.tsx`, and `app/src/lib/stores/pull-request-store.ts`. | 34, 38, 42 | Partial | A selected branch shows its pull requests, changed files, review state, and merge status from the connected account. |
| 40 | Show, rerun, and notify about pull-request checks; use `app/src/ui/check-runs`, `app/src/ui/notifications`, and `app/src/lib/ci-checks/ci-checks.ts`. | 39 | Partial | Native Core and Remotes source lists legacy statuses and modern check runs, loads job steps, exposes a check link, and provides a guarded re-run action with loading, empty, error, and cancellation states. Live GitHub account and network behavior, rerun results, and failed-check notifications remain unverified. |
| 41 | Show repository rules and secret-scanning results; use `app/src/ui/repository-rules` and `app/src/ui/secret-scanning`. | 39 | Remaining | A pull request or branch displays applicable rules and secret findings with links to the relevant GitHub detail. |

## 6. Authentication and local tool access

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 42 | Sign in to GitHub, switch accounts, sign out, and complete callbacks; use `app/src/ui/sign-in`, `app/src/ui/preferences/accounts.tsx`, `app/src/lib/auth.ts`, and `app/src/main-process/ipc-main.ts`. | 1, 9 | Partial | Native Settings lists, loads, and removes DPAPI-backed accounts and offers an explicit device-code flow behind the public `WINGIT_GITHUB_CLIENT_ID` configuration boundary, with optional `WINGIT_GITHUB_HOST` for an Enterprise host. The signed-out/unconfigured capture is verified; live device approval, profile verification, and interactive sign-out still need desktop verification. No Electron profile is read or imported. |
| 43 | Handle HTTPS credentials and Git Credential Manager; use `app/src/ui/generic-git-auth`, `app/src/lib/git/authentication.ts`, and `credential.ts`. | 34, 46 | Remaining | A credential request identifies the remote host, uses the configured helper or prompt, and never displays a credential value. |
| 44 | Handle SSH hosts, keys, passphrases, and passwords; use `app/src/ui/ssh` and `app/src/lib/ssh`. | 34, 46 | Remaining | SSH setup and failures identify the host and next action while keeping private key and password values out of the UI and logs. |
| 45 | Handle SAML reauthentication, expired tokens, and untrusted certificates; use `app/src/ui/saml-reauth-required`, `invalidated-token`, `untrusted-certificate`, and `app/src/lib/git/authentication.ts`. | 42, 43, 44 | Remaining | Each error offers the matching recovery path and blocks transport until the required user action completes. |
| 46 | Use the internal bundled Git runtime and report a missing or unsupported runtime; the Electron references are `app/src/lib/git/dugite.ts`, `app/src/lib/git/spawn.ts`, and the bundled runtime setup. `app/src/ui/install-git` covers the separate external terminal Git flow. | 1 | Verified | The final `publish-fixed` artifact and identical `run-fixed` and restored `run-recovery` copies contain the pinned 384-file Dugite tree plus license and provenance assets. A Release build passed with zero warnings and errors, while an intentionally wrong expected hash failed with `MSB3952`. With system Git unavailable on the child PATH, the copied app performed bundled Git version, repository, status, and diff operations; the trace records `git.exe` under `WinGit.Native.exe`. Disposable missing and invalid runtime copies showed a visible recovery window with the bundled path and restore/reinstall and restart action, preserved the fixture, and returned to the normal Changes view after restoring the valid executable. See `native/artifacts/runtime-task3/runtime-task3-report.md` and its `captures` directory. Numeric version policy, install/update, signing, and broader anti-tampering behavior remain outside this row. |

## 7. Multiple repositories and repository extensions

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 47 | Manage multiple repositories, aliases, and removal; use `app/src/ui/repositories-list`, `app/src/ui/change-repository-alias`, `app/src/ui/remove-repository`, and `app/src/lib/stores/repositories-store.ts`. | 4 | Partial | Native settings now keep unlimited normalized recent paths, store case-insensitive aliases, expose Rename and Remove actions in Settings, and leave repository files and the active context untouched when a row is removed. The focused in-memory probe retained 14 synthetic recents, round-tripped an alias with case and a trailing separator, preserved profile fields, and cleared only the removed recent and alias. A copied DC114 artifact removed fixture B's row and alias while keeping B open, preserving its file, keeping HEAD `f60d80e8d3013faa69cd7070cb36f94052039810` and Git clean, and leaving History usable; see `native/artifacts/screenshots/native-dc114-history-dark-after-forget-20260905.jpg`. Broader current-source rebuild and interactive coverage remain. |
| 48 | Create, apply, pop, drop, and switch stashes; use `app/src/ui/stash-changes`, `app/src/ui/stashing`, and `app/src/lib/git/stash.ts`. | 11, 46 | Partial | Stash actions identify the affected changes, handle conflicts, and refresh the worktree and stash list. |
| 49 | Add, delete, rename, and select worktrees; use `app/src/ui/worktrees`, `app/src/ui/toolbar/worktree-dropdown.tsx`, and `app/src/lib/git/worktree.ts`. | 11, 47 | Partial | Worktree operations update the list and current path while refusing unsafe deletion of a checked-out worktree. |
| 50 | Show submodule state, update submodules, and open submodule diffs; use `app/src/lib/git/submodule.ts` and `app/src/ui/diff/submodule-diff.tsx`. | 11, 13 | Partial | A repository with submodules identifies each path and revision, and update errors identify the submodule that failed. |
| 51 | Initialize and operate Git LFS with progress; use `app/src/ui/lfs`, `app/src/lib/git/lfs.ts`, and `app/src/lib/progress/lfs.ts`. | 34, 46 | Remaining | LFS setup reports availability and progress, and LFS transport errors identify the affected operation. |
| 52 | Provide no-repository, welcome, and tutorial flows; use `app/src/ui/no-repositories`, `app/src/ui/welcome`, and `app/src/ui/tutorial`. | 3, 4 | Remaining | A first launch with no repositories offers the documented create, add, and clone paths and does not show an empty broken view. |

Current work unit 50 evidence: the 65C1 copied artifact showed parent and
child revisions, rejected a stale gitlink before update, updated the child
after refresh, disabled Update for a dirty child, and opened the nested
repository while preserving its text diff. See
`native/artifacts/screenshots/native-65c1-submodule-stale-target-light-20260905.jpg`,
`native/artifacts/screenshots/native-65c1-submodule-update-success-light-20260905.jpg`,
`native/artifacts/screenshots/native-65c1-submodule-dirty-update-disabled-light-20260905.jpg`,
and
`native/artifacts/screenshots/native-65c1-submodule-open-preserved-edit-light-20260905.jpg`.
Recursive, network, uninitialized, and broader failure cases remain unverified.

The `12A1` copied artifact also showed the current submodule diff view. It
persisted the selected image mode after restart, opened a dirty child from
Changes while preserving `library.txt`, and showed a History-added gitlink
with the full new revision. A Dark History pass opened the initialized child
and preserved its edit. The parent fixture stayed at
`d8764ff6444dbed832de41f9af8a3fe0e5e4d298` with only `modules/test-library`
modified, and the child stayed at
`6b3b21d6fcae8d4263ccc2b626d2f5ed021c786c` with only `library.txt` modified;
the library hash remained
`E17CDB7B2A78E614224B8A5BD41FAB7EC54301150A8355C31B195C2AA119CF72`.
Recursive, network, uninitialized, and broader failure cases remain
unverified. A post-await workspace guard is in the current source but was
added after the `12A1` publish and needs a later build check.

## 8. Settings, themes, and accessibility

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 53 | Provide one light palette, one dark palette, and a `System` choice between them; use `app/src/ui/preferences/appearance.tsx` and `app/src/ui/lib/application-theme.ts`. | 3 | Partial | Changing the theme updates the whole native window and persists across restart. The `9B98` artifact retained Light after restart, but its caption buttons and History dialog exposed theme mismatches. Current source adds effective-theme, system-color, high-contrast, inactive-titlebar, and dialog or popup synchronization; the DC114 dark Settings capture is recorded at `native/artifacts/screenshots/native-dc114-settings-dark-20260905.jpg`, while an actual High Contrast switch and the next current-source visual pass remain unverified. |
| 54 | Persist native settings and recent repositories; use `native/WinGit.Native/NativeSettings.cs` and `app/src/lib/databases/repositories-database.ts` as references. | 4, 53 | Partial | Native settings are stored under the `WinGit.Native` data boundary, recover from malformed data, and never import Electron or GitHub auth state. |
| 55 | Provide Git identity, editor, and integration settings; use `app/src/ui/preferences/git.tsx`, `app/src/ui/editor`, and `app/src/ui/preferences/integrations.tsx`. | 3 | Partial | A setting change is visible to the next matching Git or editor action and reports invalid configuration before execution. Core reads and writes scoped Git identity and default-branch values while preserving unrelated configuration, covered by `GitRepositoryConfigurationTests`; Native also discovers and persists editor and shell selections with guarded launchers. Full Settings UI coverage, next-action desktop proof, and invalid-configuration interaction remain unverified. |
| 56 | Provide notification, prompt, and advanced settings; use `app/src/ui/preferences/notifications.tsx`, `prompts.tsx`, and `advanced.tsx`. | 53 | Remaining | Each setting changes its documented notification or prompt behavior and remains stable after restart. |
| 57 | Provide accessibility, keyboard, and zoom settings; use `app/src/ui/preferences/accessibility.tsx`, `app/src/ui/keyboard-shortcut`, and `app/src/ui/window/zoom-info.tsx`. | 3 | Remaining | Keyboard navigation reaches every action, controls expose accessible names, and zoom changes are visible without layout loss. |

## 9. Codex account and review features

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 58 | Provide Codex login, logout, and account state; use `app/src/ui/preferences/codex.tsx`, `app/src/lib/stores/codex-account-store.ts`, and `app/src/main-process/codex-authorization.ts`. | 3 | Partial | The native account screen shows signed-out and signed-in states, offers browser and device-code login, correlates completion, and keeps credentials outside the Electron profile. Interactive authentication still needs desktop verification. |
| 59 | Show Codex usage and rate limits; use `app/src/ui/preferences/codex.tsx` and `app/src/main-process/codex-rate-limits.ts`. | 58 | Partial | Native usage bars and reset text cover available, near-limit, exhausted, and unavailable states without blocking Git workflows. Live account usage still needs a signed-in verification. |
| 60 | Provide one shared Codex model and reasoning selector; use `app/src/lib/codex-model-selection.ts` and `app/src/ui/preferences/codex.tsx`. | 58 | Partial | Native settings persist one model and effort choice, validate it against a returned catalog, and preserve it when the catalog is unavailable. The commit-message caller now consumes the shared selection; review callers still need wiring. |
| 61 | Generate an editable commit message; use `app/src/lib/codex-commit-message-generator.ts`, `app/src/ui/generate-commit-message`, and `app/src/ui/changes/commit-message.tsx`. | 12, 19, 58, 60 | Partial | The native Changes panel sends the exact raw staged patch; amend uses the HEAD-parent-to-index replacement diff. It shows progress/cancellation and safe failure states, guards the repository, full index fingerprint, HEAD, selected model/effort, and draft text, and populates a reviewable title and description without committing. Native repository-rule loading, live Codex generation, and interactive acceptance still need verification. |
| 62 | Review merge, rebase, and cherry-pick conflicts with per-file Codex suggestions; use `app/src/lib/codex-conflict-suggestion-generator.ts` and `app/src/ui/multi-commit-operation/dialog`. | 27, 28, 29, 58, 60 | Remaining | Suggestions are review-only, show the affected file, and require an explicit per-file user choice before any repository write. |
| 63 | Review selected changes with file and line findings; use `app/src/lib/codex-selected-changes-review-generator.ts` and `app/src/ui/changes/selected-changes-review-dialog.tsx`. | 12, 13, 58, 60 | Partial | Native source captures the selected files and line findings, keeps the review receipt separate from commit-message consent, guards stale selection and repository state, and exposes review-only results without a Git mutation. Focused compiled consent checks cover separation, path normalization, JSON round-trip, expiry, and invalid receipts. Live Codex generation, authentication, and desktop review interaction remain unverified. |

## 10. Packaging, release, and updates

| # | Work unit and source | Depends on | Native status | Acceptance criteria |
| --- | --- | --- | --- | --- |
| 64 | Package the native app and publish release notes and acknowledgements; use `native/build.ps1`, `script/package.ts`, `app/src/ui/release-notes`, and `app/src/ui/acknowledgements`. | 1, 3 | Remaining | A clean `win-x64` output can be installed or unpacked by a user, and release notes identify the native build without changing the Electron package. |
| 65 | Establish native signing and release gates; use `script/release-config.ts`, `README.md`, and `docs/process/win-git-preview-release-gate.md`. | 64 | Remaining | A release records signing status and fails closed when the required certificate or release evidence is absent. |
| 66 | Provide an update channel and update progress; use `app/src/main-process/squirrel-updater.ts`, `app/src/ui/installing-update`, `app/src/ui/lib/update-store.ts`, and `app/src/lib/get-updater-guid.ts`. | 64, 65 | Remaining | An available update can be verified, downloaded, and installed with visible progress, and an unavailable or unsigned update is not applied silently. |

Rows marked `Partial` have useful native code or evidence, but their
acceptance criteria still have open work. Rows marked `Remaining` are part of
the parity target and have no native completion claim. A native build that
opens a window does not prove full feature parity.

## Architecture boundary

The native app uses WinUI 3 controls for interaction and a small C#
`GitRepositoryService` in `WinGit.Core` for Git for Windows command calls. The
first service shape should let callers ask for one operation at a time, such
as status, diff, or history, and receive typed results. Process launch,
argument handling, encoding, cancellation, timeouts, path normalization, and
credential redaction stay inside the service.

`CodexAppServerClient` provides a separate local stdio JSON-RPC boundary for
account, login, logout, rate-limit, and model state. Native Settings owns one
lazy client and keeps its Codex home under the native data boundary. Generation
and review callers still need to consume the shared selector.

The Electron TypeScript modules remain a behavior reference. A reusable Node
route would need open, status, diff, and history RPC methods, a standalone Node
process lifecycle, and a decomposition of renderer state, Git performance
hooks, the Git trampoline, and `AppStore` imports. The C# service keeps those
concerns behind one native boundary with fewer cross-process assumptions.

The read-only first slice limits the initial risk. Core checks can use
temporary repositories to prove Git parsing and process behavior before each
write-capable work unit is added. Native authentication stays separate from
the Electron profile, and the native app does not host WebView2 or embed
Electron or React components.

Native settings use `%LOCALAPPDATA%\WinGit.Native\settings.json`. Native Codex
uses `%LOCALAPPDATA%\WinGit.Native\codex` and does not import the Electron
profile. The UI does not persist authorization URLs, device codes, tokens, or
refresh-token requests.

Copy a native build output tree to a temporary directory before launching it for a
capture. A later publish can replace files while the app is still running.
Keep each capture output outside the copied tree so the PNG remains available
after the process exits.
