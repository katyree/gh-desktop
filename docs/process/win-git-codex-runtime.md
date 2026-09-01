# WinGit Codex Runtime Decision

Status: validated on 2026-08-31 for Windows x64.

## Decision

WinGit pins `@openai/codex` at exactly `0.151.0`. The app does not use a
version range and does not let the runtime update itself. A runtime upgrade is
a reviewed dependency change that updates `app/package.json` and
`app/yarn.lock`, reruns the isolated probe, and rebuilds the license artifact.

This is the official Codex CLI package. Its launcher selects a platform package
and starts the native `codex` binary. OpenAI publishes Windows packages for
x64 and ARM64 from the same versioned package family. The corresponding source
tag is
[`rust-v0.151.0`](https://github.com/openai/codex/tree/rust-v0.151.0).

## Windows package inventory

| Package | Target | Published unpacked size |
| --- | --- | ---: |
| `@openai/codex@0.151.0` | Platform launcher | 11,652 bytes |
| `@openai/codex@0.151.0-win32-x64` | `x86_64-pc-windows-msvc` | 414,064,796 bytes |
| `@openai/codex@0.151.0-win32-arm64` | `aarch64-pc-windows-msvc` | 357,782,177 bytes |

The platform archive contains `codex.exe`, `codex-code-mode-host.exe`,
`rg.exe`, `codex-command-runner.exe`, and
`codex-windows-sandbox-setup.exe`. The x64 probe copied the platform `vendor`
payload into a clean directory and measured 414,060,951 bytes. The small
difference from npm's unpacked-size field is package metadata outside that
payload.

WinGit resolves the architecture package installed by the pinned launcher and
starts `vendor/<target-triple>/bin/codex.exe` directly. It must not depend on a
global `codex.cmd`, the user's `PATH`, or an existing Codex desktop
installation.

## Reproducible probe

Run:

```powershell
yarn probe:codex-runtime
```

The probe performs these checks:

1. Confirms that `app/package.json` pins an exact version and that the installed
   launcher and architecture package match it.
2. Copies only the selected native `vendor/<target-triple>` payload into a new
   temporary directory.
3. Creates an isolated `CODEX_HOME` and removes credential-bearing environment
   variables from the child process.
4. Starts `codex.exe app-server --stdio`, completes `initialize`, sends
   `initialized`, and completes `account/read` with token refresh disabled.
5. Stops the exact child process and removes the temporary directory.

The probe reports only boolean account state. It never prints or reuses tokens,
account identity, or subscription details. OpenAI documents the newline-delimited
JSON protocol, initialization lifecycle, and `account/read` method in the
official
[`codex-app-server` README](https://github.com/openai/codex/blob/rust-v0.151.0/codex-rs/app-server/README.md).

## License and redistribution

The Codex source and npm packages identify the runtime as Apache-2.0. The
version-pinned license is available in the
[`rust-v0.151.0` source tree](https://github.com/openai/codex/blob/rust-v0.151.0/LICENSE).
That license permits redistribution subject to its conditions, including
providing the license and preserving applicable notices. This is a statement
about the software license, not permission to resell or redistribute access to
OpenAI services.

WinGit's license generator includes the full Apache-2.0 text for both the
launcher and the installed platform package. The platform package has no npm
dependencies, but its native binaries incorporate Rust crates and bundle
`rg.exe`. OpenAI's versioned npm archive does not include a separate SBOM or
third-party notice bundle. The upstream source enforces an explicit permissive
license allowlist in
[`codex-rs/deny.toml`](https://github.com/openai/codex/blob/rust-v0.151.0/codex-rs/deny.toml),
but that is not a substitute for artifact-specific third-party notices.

Public distribution remains blocked until release review confirms that the
Apache text plus upstream artifact contents satisfy every native transitive
notice obligation, or WinGit adds an audited notice bundle generated from the
exact Codex source tag.

## ChatGPT sign-in boundary

The App Server protocol exposes a Codex-managed ChatGPT browser login, stores
and refreshes its tokens, and labels that authentication mode as recommended.
OpenAI also documents ChatGPT-plan use through its named Codex clients in
[Using Codex with your ChatGPT plan](https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan).

No primary source reviewed for this decision explicitly authorizes a separately
branded, publicly distributed third-party desktop application to initiate that
managed ChatGPT OAuth flow. OpenAI's
[Terms of Use](https://openai.com/policies/terms-of-use/) continue to govern
individual ChatGPT service access and separately restrict distribution of the
Services and programmatic extraction.

Therefore:

- Local development may use the documented App Server login protocol.
- WinGit must let Codex own the OAuth flow and token persistence; WinGit must
  never inspect, copy, or log tokens.
- Public release with ChatGPT subscription sign-in is blocked pending explicit
  written confirmation from OpenAI or a published policy covering third-party
  App Server clients.

This document records an engineering release gate, not legal advice.

## Upgrade procedure

1. Review the target Codex release and source tag.
2. Run `yarn --cwd app add @openai/codex@<version> --exact --ignore-scripts`.
3. Verify package license, Windows x64 and ARM64 publication, archive contents,
   and unpacked sizes against npm metadata.
4. Update the pinned license source and this inventory.
5. Run `yarn probe:codex-runtime`, the script compile, the license build, and a
   packaged smoke test before merging the upgrade.
