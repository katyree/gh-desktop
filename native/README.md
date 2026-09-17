# Native WinGit

`native` contains the separate WinUI 3 desktop app. It uses the
`WinGit.Native` identity, targets `win-x64`, and publishes an unpackaged,
self-contained Windows App SDK application.

The Electron app remains under `app`. Native work leaves that app, its saved
authentication state, and its session data in place. The Electron source is a
behavior reference while the native app uses WinUI controls and the C# core.

## Prerequisites

Use these tools on Windows:

- Windows 10 version 1809 or later ([Windows App SDK versioning](https://learn.microsoft.com/en-us/windows/apps/get-started/versioning-overview)).
- .NET SDK 8.0.423 or another compatible .NET 8 SDK.
- Git for Windows with `git.exe` on `PATH`.

The native project uses Microsoft.WindowsAppSDK `2.2.0` and
Microsoft.Windows.SDK.BuildTools `10.0.26100.4654`. Restore these pinned
packages from NuGet. The current verification host has both packages in its
local NuGet cache.

Check the command-line prerequisites from the `gh-desktop` repository root:

```powershell
dotnet --version
git --version
```

The build may report `NU1900` when the NuGet vulnerability service is
unavailable. The project keeps package auditing enabled and does not hide that
warning.

## Build and run

Run the standard `win-x64` restore, build, and publish script from the
`gh-desktop` repository root:

```powershell
.\native\build.ps1 -Configuration Release -Runtime win-x64 -OutputPath .\native\artifacts\win-x64
```

The published files go to `native\artifacts\win-x64`. Start the executable
with a repository path when you want to open a repository at launch:

```powershell
& .\native\artifacts\win-x64\WinGit.Native.exe C:\src\example
```

The `app` directory remains unchanged by this command.

## Capture a view

Copy the published tree before launching a capture. A later publish can
replace files in a running tree. Write each PNG outside the copied tree.

```powershell
$repoRoot = (Resolve-Path .).Path
$publishRoot = Join-Path $repoRoot 'native\artifacts\win-x64'
$runRoot = Join-Path $env:TEMP ("wingit-native-run-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
Copy-Item -Path (Join-Path $publishRoot '*') -Destination $runRoot -Recurse
$capturePath = Join-Path $repoRoot 'native\artifacts\screenshots\changes-light.png'
New-Item -ItemType Directory -Force -Path (Split-Path $capturePath) | Out-Null
& (Join-Path $runRoot 'WinGit.Native.exe') `
  --capture $capturePath `
  --view changes `
  --repository $repoRoot `
  --theme light
```

Use `--view history`, `settings`, `branches`, `worktrees`, or `stashes` for the
other views. Add `--compare-branch <local-branch>` to a History capture to show
the captured Ahead and Behind counts for that branch. Use `--theme dark` for
the dark palette. The native appearance scope has one light palette and one
dark palette. The `System` setting chooses between them.

Current workspace capture artifacts include:

- `native/artifacts/screenshots/changes-light-03.png`
- `native/artifacts/screenshots/history-dark-02.png`
- `native/artifacts/screenshots/settings-light-01.png`
- `native/artifacts/screenshots/branches-dark-01.png`
- `native/artifacts/screenshots/worktrees-light-01.png`

These files show the native Changes, History, Settings, Branches, and Worktrees
surfaces. They are local verification artifacts and are ignored by
`native/.gitignore`.

## History selection verification (task 13)

From the task worktree, build with the runtime package roots set to the existing
`app/node_modules/dugite` and `app/node_modules/@openai/codex-win32-x64`
packages in the main checkout. Keep auditing and runtime integrity enabled.

```powershell
./native/build.ps1 -Configuration Release -Runtime win-x64 -OutputPath ./native/artifacts/task13-verified-20260917
dotnet test ./native/WinGit.Core.Tests/WinGit.Core.Tests.csproj --filter FullyQualifiedName~GitRepositoryCommitSelectionTests
```

Use synthetic repositories `history-a` and `history-b` under one fixture parent.
Each must have exactly three commits named `<name> first`, `<name> second`, and
`<name> empty` (empty HEAD). The first commit adds `history.txt` containing
`<name> first`; the second appends `<name> second`. Create fixtures with the
same Windows identity used to launch the app. Do not change global Git trust.

Copy the complete published tree to a fresh run directory. Verify `App.xbf`,
`MainWindow.xbf`, `NativeImageDiffView.xbf`, `NativeSubmoduleDiffView.xbf`,
`WinGit.Native.pri`, and `Assets/icon-logo.ico` there. After confirming no other
worker is using the native app, run with absolute paths:

```powershell
$env:WINGIT_NATIVE_SETTINGS_DIRECTORY = '<absolute-isolated-profile-directory>'
& '<absolute-run-directory>/WinGit.Native.exe' --capture '<absolute-output.png>' --view history-selection-check --repository '<absolute-fixture-parent>' --theme dark
```

The app exits automatically and writes `<absolute-output.png>.checks.txt`.
This is handler-driven real WinUI/Git verification, not mouse-driven testing.

2026-09-17 evidence, relative to this task worktree's `native/artifacts`:

- `task13-boundary-red-20260916.png.checks.txt`: navigation away failed because
  pending History work populated files/diff and replaced the Changes status.
- `task13-workspace-guard-20260916.png.checks.txt`: workspace protection passed;
  repository-open locking failed because HistoryList remained enabled.
- `task13-verified-20260917.png.checks.txt`: 33 checks passed, exit 0. Covers
  rapid commits, metadata/diff contents, clearing pending commit/file reads,
  empty commits, navigation away, repository A-to-B, refresh/open locking,
  HistoryList enabled during file/diff reads, selected navigation, visible diff
  ItemsSource, and idle state before capture.
- `task13-verified-20260917.png`: image review reported selected History
  navigation and a visible diff. Windows OCR independently read the rendered
  `history-b second` metadata, `history.txt`, `history-b first` and
  `history-b second` diff lines, and `Showing 22beeff / history.txt` status.
- Release restore/build/publish passed with zero warnings/errors; focused Core
  commit-selection tests passed 2/2; `git diff --check` passed.
- Both fixtures remained clean with HEADs
  `f1bf62a797ff26d276d9b4dc94037d10ea06e9ac` (A) and
  `4a00c6dccc77a3c9da0be6b8f4cdd65f8cd63e40` (B).

Limits: no mouse-driven interaction, fault-injected late-error test, or
image/submodule race verification. Task 14 owns comparison snapshot revalidation;
task 12 owns file-list enablement. During integration, retain both named optional
`allowHistorySelection` and task 12's `allowDiffSelection` arguments. M16–17
remain Partial in the inventory rather than claiming full interaction coverage.
Integration was not attempted: its checkout was clean at
`bf38978e43878203e5ea160a7ca7392c0e7eb894`, but exclusive coordinator ownership
could not be established from the stale local records. Artifacts and isolated
profiles are ignored and must not be committed.

## Current native slice

The captured first slice opens a local repository, shows its branch and changed
files, renders a text diff, browses commit history and committed files, and
shows native theme and recent-repository settings. The focused Core test set
currently contains 20 tests; 19 passed in the latest run: eight repository and
branch tests, one repository creation and clone test, two partial-staging
tests, two stash and worktree tests, one local transport test, and six Codex
protocol and lifecycle tests. One partial-staging test currently fails while
unstaging a new file; the Codex tests remain passing.

Run the Core tests from the repository root:

```powershell
dotnet test .\native\WinGit.Core.Tests\WinGit.Core.Tests.csproj --no-restore
```

The captured surfaces and Core tests cover a first working slice. Full staging,
partial hunk selection, history search and comparison, GitHub integration,
authentication, advanced Git operations, Codex login and generation,
packaging, signing, and updating remain in [`MIGRATION.md`](MIGRATION.md).

## Data boundaries

Native preferences use `%LOCALAPPDATA%\WinGit.Native\settings.json`.
Native Codex login is a remaining work unit and uses
`%LOCALAPPDATA%\WinGit.Native\codex` when implemented. The current Codex
client reads protocol state only. It does not log in, log out, migrate
credentials, or write tokens. The native app does not import the Electron
profile or host a WebView2 control.

For an isolated settings-only diagnostic run, set
`WINGIT_NATIVE_SETTINGS_DIRECTORY` in the app process environment to a fully
qualified, writable directory. `NativeSettingsStore` then reads and writes
only that directory's `settings.json`. When the variable is unset, the default
remains `%LOCALAPPDATA%\WinGit.Native\settings.json`. An empty or relative
value throws `InvalidOperationException` instead of falling back. The override
does not redirect GitHub account storage, Codex data, temporary files, or other
native paths.

### GitHub sign-in preview configuration

The native GitHub account flow uses an explicit device-code sign-in. A build
needs a public OAuth client ID supplied through `WINGIT_GITHUB_CLIENT_ID`; the
ID is configuration, not a secret. Leave it unset to keep sign-in unavailable.
For a GitHub Enterprise host, set `WINGIT_GITHUB_HOST` to its HTTPS origin;
when unset, the flow uses `https://github.com`.

The account screen starts sign-in only after the user chooses it. It shows the
device code and verification page, and saves an account only after the profile
has been verified. Protected account data stays in the native
`WinGit.Native` data boundary and is never read from Electron or GitHub CLI
profiles. Capture mode does not read accounts or start sign-in.
