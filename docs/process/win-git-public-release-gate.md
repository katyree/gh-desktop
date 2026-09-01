# WinGit public-release and upstream-sync gate

Date: 2026-09-01
Decision: **not approved for public distribution; private local builds only**

The planned implementation pass through issue 25 is complete. The local source,
tests, production bundle, and Windows packages are internally consistent, and
the current upstream branch introduces no sync conflict. Public distribution is
still blocked by release inputs and real-environment checks that are not
available in this checkout. This is an engineering release record, not legal
advice.

## Verification matrix

| Gate | Result | Evidence or remaining action |
| --- | --- | --- |
| Current upstream | Pass | `upstream/development` and `origin/development` both resolve to `b17e06dd0f0d9a45807eb39a51d223f52eb14da9`; divergence is `0 0`. |
| Upstream rehearsal | Pass | `git merge-tree --write-tree HEAD upstream/development` produced tree `05f5faaae54840d80a72d1a460e25abbced0c25b` without a conflict. No rehearsal branch was created because branch creation was not authorized and upstream had not moved. |
| Full unit suite | Pass | 1,489 tests: 1,488 passed, 1 skipped, 0 failed. |
| Lint and type checks | Pass | `corepack yarn lint`, `corepack yarn tsc`, and `corepack yarn compile:script` completed successfully. |
| Production build | Pass | `corepack yarn build:prod` completed successfully. |
| Windows package | Pass | Production packaging produced the three expected x64 artifacts and `wingit-release.json`. |
| Commit-message UI | Pass with deterministic runtime | Unpackaged Electron E2E passed generation, mid-turn cancellation, invalid-output recovery, and exhausted-quota behavior through renderer, IPC, and an isolated fake App Server. |
| Packaged signed-out UI | Pass | The real packaged app reached Codex settings while signed out: 1 test passed and 4 authenticated cases were skipped. |
| Live ChatGPT account | Blocked | Browser/device authorization, real generation, real cancellation, sign-out persistence, and authenticated diagnostic scanning require the owner to complete sign-in. |
| Conflict suggestions | Partial pass | The provider-neutral generator, strict validation, partial-result handling, progress, cancellation, review-only behavior, and write-selection tests pass. Packaged accept, reject, cancellation, and partial failure still require a signed-in conflict fixture. |
| Production bundle isolation | Pass | The package contains no `out/copilot`, stale compiler `out/app`, macOS `Assets.car`, `@github/copilot-sdk`, or Copilot CLI marker. |
| Update configuration | Pass while disabled | The release manifest reports `updateEndpointConfigured: false` and `automaticUpdatesEnabled: false`; production update URLs require explicit HTTPS configuration. |
| Signed update path | Blocked | No owned HTTPS feed, WinGit signing identity, signed old/new pair, tamper test, staged rollout, or rollback exercise is available. |
| Installer signatures | Fail | The EXE and MSI are `NotSigned`; `dotnet nuget verify --all` reports the full package is not signed. |
| Clean Windows VM | Blocked | Fresh install, first launch, GitHub Desktop coexistence, update, uninstall, and installed-file digest checks have not run on a clean machine. |
| Branding and privacy audit | Pass locally | Windows/shared artwork and production copy use WinGit. Remaining GitHub Desktop mentions are required upstream acknowledgement or legal terms. A real authenticated log scan is still pending. |
| Name and distribution clearance | Blocked | Public search was not a professional trademark clearance. Explicit permission for third-party ChatGPT subscription authentication and runtime redistribution in this separately branded app remains unconfirmed. |
| Fresh-clone reproducibility | Blocked | The documented contributor flow has not yet run from a fresh clone under a clean Windows account. |

## Local artifact evidence

These digests identify unsigned local test artifacts. They are not trusted
release digests and must not be presented as published binaries.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `WinGitSetup-x64.exe` | 330,509,824 | `833B379EC71070A60685CDDFEF9F81ED223C984BCECBD69C811F78DE2BED86DA` |
| `WinGitSetup-x64.msi` | 330,493,952 | `E2E58E1E5E081A7C0021A9D5CE3AB31870BC7D583C4D82720FD585333C8EF1C1` |
| `WinGit-3.6.5-beta1-x64-full.nupkg` | 330,525,431 | `EBC0891055114EE164E9CCE6618D2B514FFB878B300C596F77090444A53B143E` |

Release manifest:

```json
{
  "schemaVersion": 1,
  "product": "WinGit",
  "version": "3.6.5-beta1",
  "channel": "development",
  "architecture": "x64",
  "updateEndpointConfigured": false,
  "automaticUpdatesEnabled": false
}
```

## Public-release blockers

A release candidate may be promoted only after all of these are closed:

1. Obtain a WinGit code-signing identity and sign the installer, MSI, NuGet
   package, and update payloads.
2. Confirm in writing that this integration may offer ChatGPT subscription
   authentication and redistribute the pinned Codex runtime.
3. Complete professional name and trademark clearance.
4. Run live sign-in, commit generation, cancellation, sign-out, persistence,
   conflict accept/reject/cancel/partial-failure, and sanitized-log checks.
5. Host the HTTPS update channel and prove signed old-to-new update, tamper
   rejection, staged rollout, and rollback.
6. Run the documented build from a fresh clone and clean Windows account.
7. On a clean Windows VM, install beside GitHub Desktop, compare application
   identity and data paths, update, uninstall, and verify GitHub Desktop is
   unchanged.
8. Independently download the candidate, reproduce its published digests, and
   verify every signature before publication.

macOS is outside this Windows-first gate. The legacy macOS `Assets.car` must be
regenerated and separately verified before any macOS package is distributed as
WinGit.

## Repeatable commands

```powershell
git fetch upstream --prune
git rev-list --left-right --count origin/development...upstream/development
git merge-tree --write-tree HEAD upstream/development
corepack yarn test:unit
corepack yarn lint
corepack yarn tsc
corepack yarn compile:script
corepack yarn build:prod
$env:NODE_ENV = 'production'
corepack yarn package
Get-FileHash dist\WinGitSetup-x64.exe -Algorithm SHA256
Get-FileHash dist\WinGitSetup-x64.msi -Algorithm SHA256
Get-FileHash dist\WinGit-3.6.5-beta1-x64-full.nupkg -Algorithm SHA256
Get-AuthenticodeSignature dist\WinGitSetup-x64.exe
Get-AuthenticodeSignature dist\WinGitSetup-x64.msi
dotnet nuget verify dist\WinGit-3.6.5-beta1-x64-full.nupkg --all
```

Deterministic authenticated-state UI checks use the local fake App Server only;
they do not prove OpenAI service behavior:

```powershell
npx cross-env DESKTOP_E2E_APP_MODE=unpackaged DESKTOP_E2E_FAKE_CODEX=1 npx playwright test --config app/test/e2e/playwright.config.ts app/test/e2e/codex-commit-generation.e2e.ts
npx cross-env DESKTOP_E2E_APP_MODE=unpackaged DESKTOP_E2E_FAKE_CODEX=1 DESKTOP_E2E_CODEX_INITIAL_MODE=exhausted npx playwright test --config app/test/e2e/playwright.config.ts app/test/e2e/codex-rate-exhausted.e2e.ts
```

## Policy references

- [Using Codex with a ChatGPT plan](https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan/)
- [Codex App Server authentication](https://github.com/openai/codex/blob/rust-v0.151.0/codex-rs/app-server/README.md)
- [Codex Apache 2.0 license](https://github.com/openai/codex/blob/rust-v0.151.0/LICENSE)
- [OpenAI Terms of Use](https://openai.com/policies/terms-of-use/)
- [GitHub Open Source Applications Terms and Conditions](https://docs.github.com/en/site-policy/github-terms/github-open-source-applications-terms-and-conditions)
