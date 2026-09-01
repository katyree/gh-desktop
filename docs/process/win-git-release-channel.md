# WinGit update and release channel

Status: implementation ready; public infrastructure and signed clean-VM
verification are external release gates.

## Safety defaults

WinGit has no production update URL by default. A release build contacts no
update service unless `WINGIT_ENABLE_UPDATES=1` and `WINGIT_UPDATES_URL` names an
HTTPS Squirrel feed. Background checks require the separate
`WINGIT_ENABLE_AUTOMATIC_UPDATES=1` gate. Keep that gate off until the signed
old-to-new and tamper tests below pass.

The test-only `DESKTOP_E2E_UPDATES_URL` may use a local HTTP server. It is not a
production release setting.

Publishable Windows CI builds also require these WinGit-owned values:

- `WINGIT_AZURE_SIGNING_ENDPOINT`
- `WINGIT_AZURE_SIGNING_ACCOUNT`
- `WINGIT_AZURE_SIGNING_PROFILE`

The build fails instead of falling back to GitHub Desktop's signing identity.
Never commit signing credentials or access tokens.

## Release artifacts

`corepack yarn package` emits WinGit-specific installer and Squirrel names plus
`dist/wingit-release.json`. The manifest records the product, version, channel,
architecture, artifact names, endpoint presence, and automatic-update state.
Publish the matching `RELEASES`, full package, optional delta package, and setup
artifacts to a WinGit-owned HTTPS origin. Do not reuse GitHub Desktop's feed,
bucket, application ID, certificate, or shortcuts.

## Staged rollout

1. Build and sign a candidate from a clean checkout.
2. Verify Authenticode on the setup executable, installed executable, and every
   signed executable shipped with the app.
3. Publish the full package and `RELEASES` to a private canary route.
4. Upgrade one clean VM from the previous signed WinGit version. Compare file
   digests, application IDs, shortcuts, protocol handlers, and GitHub Desktop's
   unchanged installation.
5. Copy the exact verified artifacts to a limited rollout route. Expand the
   server-side cohort only after crash, launch, and update checks are clean.
6. Enable background checks only in a later build by setting
   `WINGIT_ENABLE_AUTOMATIC_UPDATES=1`.

## Tamper test

1. Copy the canary feed to an isolated test route.
2. Flip one byte in the update package without changing `RELEASES`.
3. Point an older test VM at that route and check for updates.
4. Record that Squirrel rejects the package hash and that the installed version,
   executable digest, shortcuts, and protocol registration remain unchanged.
5. Treat any installation or partial replacement as a release-blocking failure.

## Rollback

Stop rollout by removing the candidate from the feed and restoring the last
verified `RELEASES` plus its immutable artifacts. Do not mutate an already
published package in place. If a bad version is already installed, publish a
new, higher patch version containing the rollback; Squirrel does not use a
lower version as an automatic downgrade. Keep automatic updates disabled until
the recovery version passes the same canary and tamper checks.

## Current external gates

- No WinGit-owned HTTPS Squirrel service is configured.
- No WinGit code-signing identity or certificate is available in this checkout.
- No clean Windows VM is connected for signed old-to-new, tamper, coexistence,
  and rollback verification.

Until those gates close, local packages are private test builds and the release
must not enable automatic updates.
