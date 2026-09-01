# WinGit commit MVP preview release gate

Date: 2026-09-01  
Decision: **not ready for public preview; private local builds only**

The commit-message MVP works in deterministic integration tests and in the
packaged application while signed out. Public distribution remains blocked by
unresolved third-party ChatGPT authentication permission, unsigned installers,
native redistribution-notice review, and clean-machine verification. This is a
release-engineering record, not legal advice.

## Verification matrix

| Gate | Result | Evidence or remaining action |
| --- | --- | --- |
| Focused unit and integration tests | Pass | Commit generation, App Server, account, authorization, IPC, privacy, rate-limit, and UI tests pass. The static production-bundle commit flow passed twice. |
| Full unit suite | Pass | 1,633 tests: 1,632 passed, 1 skipped, 0 failed in 158.083 seconds. |
| Formatting and lint | Pass | `corepack yarn lint` completed successfully. |
| Development build | Pass | `corepack yarn build:dev` completed in 103.47 seconds. |
| Production renderer build | Pass | `corepack yarn build:prod` completed in 111.34 seconds. |
| Windows package | Pass | `NODE_ENV=production corepack yarn package` completed in 334.57 seconds after disabling delta generation when no update feed is configured. |
| Packaged App Server startup | Pass | The packaged x64 app started the pinned Codex runtime and reached the signed-out Codex settings screen without a global Codex installation. |
| Clean Windows VM | Blocked | No clean VM is available. Installer, first launch, sign-in, generation, uninstall, and GitHub Desktop coexistence remain unverified on a clean machine. |
| Authenticated ChatGPT flow | Blocked | Browser/device authorization and live generation require the owner to complete ChatGPT authentication. Cancellation, failure, and privacy log checks remain pending against a real signed-in request. |
| Installer signature | Fail | The EXE and MSI report `NotSigned`; no WinGit code-signing certificate is configured. |
| Runtime redistribution notices | Blocked | JavaScript license output includes the pinned Codex packages, but the bundled native executable and its transitive notices still need a release/legal audit. |
| Third-party ChatGPT authentication | Blocked | App Server documents caller-driven ChatGPT authorization, but explicit permission for a separately branded public desktop client to offer subscription authentication has not been confirmed. |
| Product identity and trademark | Pass for current package | The package uses WinGit identity and does not use GitHub's name or logo as its product identity. Required attribution remains. Re-audit after issue 24. |
| Privacy logging | Partial pass | Fake-server success, cancellation, and invalid-output logs contain timing and status only; selected content, responses, paths, and token-shaped fields were absent. Repeat after real authentication. |

## Package evidence

The x64 Squirrel package contains the pinned `@openai/codex@0.151.0` runtime,
its win32-x64 package, and generated JavaScript license notices.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `WinGitSetup-x64.exe` | 444,350,464 | `D3C2EBB57594DB52A59F509E29FC8FD5383AA84A825A2128FE58B79049849075` |
| `WinGitSetup-x64.msi` | 443,981,824 | `B75214FE323A97B39FD0108FF2D10EFC70569651D07581A3E1B247593A85AED4` |
| `WinGit-3.6.5-beta1-x64-full.nupkg` | 444,903,493 | `6FC63CD2DAE81AA576103FB95D0E07D40EDFC47EDC6D0282FB27427EA2CEBCAD` |

These digests identify local, unsigned test artifacts. They are not release
digests and must not be published as trusted binaries.

## Performance observations

- Deterministic fake-server generation completed in 15-18 milliseconds.
- Deterministic cancellation completed in 152 milliseconds.
- The packaged signed-out settings test body completed in 3.2 seconds.
- Squirrel packaging completed in 334.57 seconds.

The fake-server numbers measure application plumbing, not model latency. The
settings result is not a controlled cold-start benchmark.

## Known limits

- The current release target is Windows x64 only.
- Installers are unsigned and approximately 444 MB.
- No WinGit update feed or signed delta-update path exists yet.
- Clean-machine installation and coexistence remain unverified.
- Live ChatGPT authentication, generation, cancellation, and sanitized logging
  require a human sign-in pass.
- Conflict suggestions still use the legacy Copilot implementation until issues
  19-22 are complete.
- Public distribution remains blocked until the legal, authentication,
  redistribution, signing, and clean-machine gates above are closed.

## Policy references

- [Using Codex with a ChatGPT plan](https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan/)
- [Codex App Server authentication](https://github.com/openai/codex/blob/rust-v0.151.0/codex-rs/app-server/README.md)
- [Codex Apache 2.0 license](https://github.com/openai/codex/blob/rust-v0.151.0/LICENSE)
- [OpenAI Terms of Use](https://openai.com/policies/terms-of-use/)
- [GitHub Open Source Applications Terms and Conditions](https://docs.github.com/en/site-policy/github-terms/github-open-source-applications-terms-and-conditions)

## Commands used

```powershell
corepack yarn test:unit
corepack yarn lint
corepack yarn build:dev
corepack yarn build:prod
$env:NODE_ENV = 'production'
corepack yarn package
$env:DESKTOP_E2E_APP_PATH = (Resolve-Path 'dist\WinGit-win32-x64\WinGit.exe').Path
Remove-Item Env:DESKTOP_E2E_FAKE_CODEX -ErrorAction SilentlyContinue
.\node_modules\.bin\playwright.cmd test app/test/e2e/codex-settings.e2e.ts --config app/test/e2e/playwright.config.ts
Get-FileHash dist\WinGitSetup-x64.exe -Algorithm SHA256
Get-FileHash dist\WinGitSetup-x64.msi -Algorithm SHA256
Get-FileHash dist\WinGit-3.6.5-beta1-x64-full.nupkg -Algorithm SHA256
Get-AuthenticodeSignature dist\WinGitSetup-x64.exe
Get-AuthenticodeSignature dist\WinGitSetup-x64.msi
```
