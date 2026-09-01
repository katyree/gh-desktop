# WinGit

WinGit is a Windows-first Git client based on the open-source GitHub Desktop
codebase. It keeps GitHub Desktop's familiar repository workflow and replaces
its Copilot features with bounded Codex features that use Codex-managed ChatGPT
sign-in.

Current AI features:

- generate an editable commit message from the changes selected for a commit;
- generate review-only conflict suggestions with explicit per-file choices;
- show ChatGPT account and usage state independently from GitHub authentication.

WinGit is currently a private development build. The local installers are
unsigned, automatic updates are disabled, and public distribution remains
blocked on the release gates in
[`docs/process/win-git-preview-release-gate.md`](docs/process/win-git-preview-release-gate.md).

## Build on Windows

Use the pinned Node and Yarn versions from `.nvmrc` and `package.json`, then run:

```powershell
corepack enable
corepack yarn install
corepack yarn build:dev
corepack yarn start
```

Open **File > Options > Codex** to inspect the signed-out settings screen. A
browser sign-in is optional for building and must be completed by the user.

Focused verification:

```powershell
corepack yarn test:unit app/test/unit/codex-app-server-client-test.ts app/test/unit/ui/codex-preferences-test.tsx
corepack yarn lint
corepack yarn tsc
```

Production artifacts:

```powershell
corepack yarn build:prod
corepack yarn package
```

See the [WinGit contributor workflow](docs/contributing/win-git.md), [privacy
description](docs/privacy.md), [Codex runtime decision](docs/process/win-git-codex-runtime.md),
and [owned release channel](docs/process/win-git-release-channel.md) before
changing authentication, packaging, or update behavior.

## Project links

- Repository: <https://github.com/katyree/gh-desktop>
- Issues: <https://github.com/katyree/gh-desktop/issues>
- Releases: <https://github.com/katyree/gh-desktop/releases>

## Upstream and license

WinGit is derived from [GitHub Desktop](https://github.com/desktop/desktop),
which is distributed under the MIT License. See [LICENSE](LICENSE) and the
in-app acknowledgements for attribution and third-party notices.

The MIT license does not grant rights to GitHub trademarks. WinGit uses its own
name, application IDs, protocols, artwork, installer names, and update channel.
GitHub and GitHub Desktop are trademarks of GitHub, Inc.; WinGit is not an
official GitHub product.
