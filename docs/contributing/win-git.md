# Contributing to WinGit on Windows

## Prerequisites

- Git for Windows
- Node.js matching `.nvmrc`
- Corepack with the Yarn version declared in `package.json`
- Visual Studio Build Tools with the C++ desktop workload

No global Codex CLI, OpenAI API key, or ChatGPT sign-in is required to build the
signed-out application.

## Clean-checkout path

```powershell
git clone https://github.com/katyree/gh-desktop.git
Set-Location gh-desktop
corepack enable
corepack yarn install
corepack yarn build:dev
corepack yarn start
```

Expected result: a window titled **WinGit-dev** opens. Open **File > Options >
Codex**; the page should show the bundled runtime's signed-out state and a
ChatGPT sign-in action.

## Checks

Run the narrowest relevant tests while working, followed by:

```powershell
corepack yarn lint
corepack yarn tsc
corepack yarn test:unit
corepack yarn build:prod
```

Create local Windows installers with `corepack yarn package`. Local artifacts
are unsigned and are not public releases. The package writes
`dist/wingit-release.json`; verify it says `updateEndpointConfigured: false` and
`automaticUpdatesEnabled: false` unless you are executing the owned, signed
release process.

## Codex runtime updates

Follow [`../process/win-git-codex-runtime.md`](../process/win-git-codex-runtime.md).
Pin the exact package, update lockfiles, compile scripts, run runtime and
supervisor probes, regenerate license notices, and repeat packaged signed-out
startup. Do not inherit the user's normal Codex home, plugins, or instructions.

## Upstream sync

Follow [`../process/win-git-upstream-sync.md`](../process/win-git-upstream-sync.md).
Preserve WinGit application identity, product-service isolation, separate
GitHub and ChatGPT accounts, the provider-neutral AI contracts, and the owned
release defaults. Never resolve a conflict by restoring GitHub Desktop's update,
telemetry, Copilot runtime, signing identity, protocols, or artwork.

## Authentication boundary

GitHub OAuth and Git credential flows support repository hosting. Codex-managed
ChatGPT OAuth supports AI features. Never copy tokens between them, print token
values, automate passwords or MFA, or ask contributors to commit credentials.
