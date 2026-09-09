# Native WinGit guidance

`native` is a separate WinUI 3 application. Keep the Electron application in
`app` as the behavior reference and preserve its files and profile data.

## Build and test

Run commands from the `gh-desktop` repository root. Use .NET 8 and the
`win-x64` runtime:

```powershell
.\native\build.ps1 -Configuration Release -Runtime win-x64 -OutputPath .\native\artifacts\win-x64
dotnet test .\native\WinGit.Core.Tests\WinGit.Core.Tests.csproj --no-restore
```

The native project pins Microsoft.WindowsAppSDK `2.2.0` and
Microsoft.Windows.SDK.BuildTools `10.0.26100.4654`. Restore may report `NU1900`
when the NuGet vulnerability service is unavailable. Keep package auditing
enabled.

Copy the published tree to a temporary directory before launching a capture.
Write the PNG outside that copy. See `README.md` for the capture command and
the accepted `--view` values.

The native settings boundary is `%LOCALAPPDATA%\WinGit.Native`. Native Codex
login uses a separate `%LOCALAPPDATA%\WinGit.Native\codex` profile when that
work unit is implemented. The native app uses WinUI controls and its C# core.
The Electron renderer and profile are not runtime dependencies.

Use `WINGIT_NATIVE_SETTINGS_DIRECTORY` with a fully qualified, writable
per-process directory for isolated UI verification. The override covers only
`NativeSettingsStore` and its `settings.json`; the GitHub account store and
Codex profile keep their shared paths. Setting `LOCALAPPDATA` for a child
process does not redirect Windows `Environment.GetFolderPath`.

## Native-specific pitfalls

- Normalize CRLF and lone CR from WinUI multiline `TextBox` values to LF at the
  Core Git-message boundary before composing commit or amend messages. Preserve
  the existing NUL and summary validation.
- On the unpackaged host, subscribing to
  `AccessibilitySettings.HighContrastChanged` caused startup `COM 0x80070490`.
  Use the existing `UISettings.ColorValuesChanged` and
  `ActualThemeChanged` paths for theme updates, then verify real startup before
  adding another accessibility event subscription.

The unpackaged publish output must include the generated `App.xbf`,
`MainWindow.xbf`, `NativeImageDiffView.xbf`, `NativeSubmoduleDiffView.xbf`,
`WinGit.Native.pri`, and `Assets\icon-logo.ico` files at the
published root (with the icon under `Assets`). A successful build alone does
not prove that a copied publish tree can start; verify those files in the
published tree before launching it.

Create synthetic Git fixtures under the same Windows identity that launches a
native UI capture. Git can reject a fixture created by
`WHITEOUT\CodexSandboxOffline` when the copied app runs as `WHITEOUT\kylea`
with a dubious-ownership error; recreate the fixture in the UI launch context
instead of changing global `safe.directory` configuration.

Treat files under `native\artifacts` as local verification output. A capture
proves only the view and state that it renders. Check the migration inventory
before describing a broader workflow as complete.
