# WinGit Codex fork plan

Status: active  
Planning baseline: `b17e06dd0f0d9a45807eb39a51d223f52eb14da9` on `development`  
Last checked: 2026-08-31

## Outcome

WinGit will be a Windows-first, rebranded fork of GitHub Desktop. It will keep
GitHub Desktop's existing Git workflows and replace its GitHub Copilot-backed AI
features with Codex features authenticated through the user's ChatGPT
subscription.

The first release will generate an editable commit title and description from
the files selected for the commit. A later milestone will replace Copilot
conflict resolution with Codex conflict suggestions.

This plan does not rebuild GitHub Desktop in WinUI. Keeping the existing
Electron and TypeScript application is the shortest path to a useful product
and preserves the behavior that GitHub Desktop users already expect.

## Product decisions

1. Keep GitHub Desktop's existing Git implementation, application structure,
   and core user flows.
2. Treat WinGit as a separate product. Replace GitHub names, logos, application
   identifiers, protocol handlers, update endpoints, and support links before
   distributing a build.
3. Run Codex App Server as a child process owned by Electron's main process.
   Communicate with it through its documented JSON-RPC protocol.
4. Use Codex-managed ChatGPT OAuth. Do not ask for an OpenAI API key, reuse a
   browser session, or charge the OpenAI API separately.
5. Ship commit-message generation before conflict resolution. Do not add a
   general chat panel to the first release.

The name "WinGit" is a working name until a trademark and package-name check is
complete.

## First-release boundaries

The first public release includes:

- A rebranded Windows installer that can coexist with GitHub Desktop.
- A **Codex** settings page with sign-in, sign-out, account state, and usage
  limit state.
- Commit-message generation from only the files selected in the Changes view.
- Editable results, cancellation, clear errors, and no automatic commit.
- A disabled-by-default update channel owned by the WinGit project.

The first public release excludes:

- A WinUI rewrite or a Wino Mail visual redesign.
- A general-purpose Codex chat or autonomous coding agent.
- Automatic commits, pushes, conflict-file writes, or other Git mutations.
- GitHub Copilot authentication, billing, models, quota UI, or bundled runtime.
- macOS and Linux installers. Source changes should avoid unnecessary Windows
  coupling so those platforms remain possible later.

## Evidence and constraints

### Local source baseline

The plan is grounded in the following code at the planning baseline:

| Concern | Current source | Consequence |
| --- | --- | --- |
| AI features | `app/src/lib/stores/copilot-store.ts` | The current feature set is commit-message generation and conflict resolution. |
| Commit flow | `app/src/lib/stores/app-store.ts` | `AppStore._generateCommitMessage` already gathers the selected-file diff and writes an editable suggestion into repository state. |
| Account gate | `app/src/lib/get-account-for-repository.ts` | Current AI availability depends on a GitHub account and Copilot capability. Codex must use independent account state. |
| Settings | `app/src/ui/preferences/copilot.tsx` | Current settings hide AI controls without Copilot entitlement. WinGit needs a Codex-owned access state. |
| Custom providers | `app/src/lib/copilot/byok.ts` | Existing providers use API keys or bearer tokens and do not satisfy subscription-backed ChatGPT sign-in. |
| AI dependency | `app/package.json` | The application currently ships `@github/copilot-sdk`. Remove it after Codex paths replace both AI features. |
| Tests | `app/test/unit/stores/copilot-store-test.ts` and related files | Existing prompt, parser, cancellation, and conflict tests provide migration seams. |
| Product identity | `app/package.json`, `app/package-info.ts`, and `app/src/main-process/main.ts` | Product name, bundle ID, AppUserModelID, and protocols currently belong to GitHub Desktop. |
| Packaging | `script/package.ts` | Windows packaging and icon URLs contain GitHub-owned identity. |

### External sources

These sources were checked on 2026-08-31:

- [GitHub Desktop repository and license](https://github.com/desktop/desktop)
  confirms that the source is MIT-licensed and that GitHub trademarks are not
  included in that grant.
- [GitHub open-source application terms](https://docs.github.com/en/site-policy/github-terms/github-open-source-applications-terms-and-conditions)
  identify protected names and logos that a distributed fork must not present
  as its own identity.
- [GitHub Desktop custom provider documentation](https://docs.github.com/en/desktop/configuring-and-customizing-github-desktop/configuring-copilot-in-github-desktop)
  confirms that the upstream application supports provider configuration but
  still requires Copilot access.
- [Codex App Server documentation](https://learn.chatgpt.com/docs/app-server)
  documents JSON-RPC account methods, managed ChatGPT browser and device-code
  login, token refresh, logout, plan state, and ChatGPT rate limits.
- [Codex SDK documentation](https://learn.chatgpt.com/docs/codex-sdk)
  documents programmatic control of local Codex threads from TypeScript.
- [Codex as a platform](https://developers.openai.com/blog/codex-as-a-platform)
  recommends App Server for products that need direct control over lifecycle,
  events, interruption, tools, and approvals.

### Confirmed facts

- A host application can start Codex-managed ChatGPT OAuth through App Server.
- App Server can report ChatGPT plan state and subscription rate limits.
- GitHub Desktop already gathers the selected-file diff before asking its AI
  provider for a commit message.
- GitHub Desktop's current custom-provider path is not enough because it expects
  an OpenAI-compatible API endpoint and remains gated by Copilot entitlement.
- The GitHub Desktop MIT license permits modification and distribution, but its
  trademarks and logos require separate treatment.

### Unproven release gates

The following facts must be proven before a public installer ships:

1. The selected Codex package and runtime may be redistributed inside WinGit
   under their current licenses and terms.
2. Managed ChatGPT OAuth is supported for this distributed third-party client,
   not only for local development and internal tools.
3. An app-specific Codex data directory isolates WinGit from the user's normal
   Codex configuration while retaining reliable token refresh.
4. The packaged runtime starts on supported Windows versions and remains small
   enough for the installer and update service.

If either of the first two gates fails, stop the public-release track. A local
personal fork can still use an installed Codex CLI, but the project must not
quietly replace the subscription requirement with API-key billing.

## Architecture

### Process ownership

```text
Changes and conflict UI
        |
        v
Dispatcher and AppStore in the renderer
        |
        v
Typed Codex IPC facade
        |
        v
CodexService in Electron's main process
        |
        v
Pinned Codex App Server child process
        |
        v
Managed ChatGPT OAuth and Codex model access
```

Electron's main process owns process launch, JSON-RPC transport, login state,
and shutdown. The renderer receives typed state and commands through the
project's existing IPC wrappers. The renderer never reads OAuth tokens and
never spawns Codex directly.

### Runtime and account isolation

Use a pinned Codex runtime that ships with WinGit after the redistribution gate
passes. Start it with an app-specific Codex data directory under WinGit's
application data directory. Do not inherit the user's normal Codex plugins,
skills, hooks, approval rules, or model overrides.

WinGit owns the sign-in ceremony but not the tokens. App Server starts the
managed browser or device-code flow, stores the tokens, and refreshes them.
WinGit stores only non-secret display state needed to render settings.

Do not use externally managed ChatGPT tokens. That mode is experimental and
would make WinGit responsible for token storage and refresh.

### AI feature contract

Introduce a provider-independent application contract before adding Codex. The
first contract has one operation:

```ts
interface CommitMessageGenerator {
  generateCommitMessage(request: {
    repositoryPath: string
    diff: string
    rules: ReadonlyArray<string>
    signal: AbortSignal
  }): Promise<{ title: string; description: string }>
}
```

Add the conflict-resolution contract only when that milestone starts. Keeping
the contracts narrow avoids a general AI framework and reduces future upstream
merge conflicts.

### Commit-message request

Create one bounded Codex turn for each button click. Use the existing selected
file diff, sanitized repository commit rules, prompt tags, title limit, and JSON
parser where they remain provider-independent.

The turn must have these restrictions:

- No shell, patch, network, MCP, plugin, skill, or file-write tools.
- No approval prompts because the turn has no action capability.
- A read-only sandbox, even though the request already contains the diff.
- No implicit repository scan. The selected diff is the complete code input.
- A structured result containing only `title` and `description`.
- Cancellation connected to the existing Stop action.
- A fresh thread or disabled persistence so one repository cannot influence
  another request.

The user reviews and edits the result. Generation never invokes Git and never
commits.

### Conflict-resolution request

Reuse the current conflict context and chunking rules. Ask Codex for structured
suggestions, not direct file edits. Keep the current review screen and require a
separate user action before applying each suggested resolution.

The conflict milestone must preserve file-size limits, binary-file exclusions,
partial failure reporting, cancellation, and manual fallback.

### Privacy and trust boundary

Repository contents are untrusted model input. A diff can contain text that
looks like an instruction. Preserve the current randomized prompt delimiters,
keep trusted instructions outside the diff block, validate structured output,
and reject unexpected fields.

Before the first generation request for a repository, explain that the selected
diff and commit rules leave the machine and go to OpenAI. Do not send unselected
files, repository history, remote URLs, environment variables, Git credentials,
or local Codex configuration.

Do not log prompts, diffs, generated descriptions, OAuth URLs, authorization
codes, or tokens. Logs may contain request IDs, durations, result status, and
redacted error categories.

## Milestones

| Milestone | Included issues | Exit result |
| --- | --- | --- |
| M0: Safe fork baseline | 1-3 | A development build uses WinGit identity and does not call GitHub's update or telemetry services. |
| M1: Codex commit MVP | 4-14 | A signed-in ChatGPT user can generate and edit a commit message from selected changes. |
| M2: Distributable preview | 15-18 | Copilot runtime code is removed, a Windows package installs beside GitHub Desktop, and the flow passes a packaged smoke test. |
| M3: Codex conflict suggestions | 19-22 | Users can review, apply, or reject Codex conflict suggestions without automatic writes. |
| M4: Public release and maintenance | 23-25 | WinGit has owned update infrastructure, release documentation, and a repeatable upstream sync process. |

## Numbered implementation backlog

Each item is intended to become one issue and one reviewable pull request.
Expected size assumes a developer familiar with this repository.

### 1. Record the fork baseline and upstream policy

Status: complete on 2026-08-31. See
[WinGit upstream baseline and sync policy](win-git-upstream-sync.md).

Goal: make the fork's source, ownership, and sync rules explicit.

Scope:

- Record the upstream commit used for the fork.
- Document `origin` as the WinGit fork and `upstream` as `desktop/desktop`.
- Define how often to fetch upstream and how to record merge conflicts.
- State that unrelated upstream behavior remains unchanged.

Acceptance criteria:

- A contributor can identify the exact upstream baseline and sync procedure
  without inspecting local Git configuration.
- The policy forbids broad formatting or rename changes during upstream syncs.

Verification: follow the documented read-only commands and confirm both remotes
and the baseline commit.  
Dependencies: none.  
Expected size: half a day.

### 2. Rebrand package metadata and development identity

Status: complete on 2026-08-31.

Goal: make a development build identify itself as WinGit.

Scope:

- Change the product, package, company, repository, author, and bundle metadata.
- Assign WinGit-specific development and production bundle IDs.
- Assign a WinGit Windows AppUserModelID.
- Keep development and production identities distinct.

Acceptance criteria:

- The application title and About dialog say WinGit.
- A development build uses WinGit process and application identity.
- GitHub Desktop and a WinGit development build can run side by side.

Verification: run `yarn build:dev`, launch the build, and inspect the title,
About dialog, process identity, and taskbar grouping.  
Dependencies: 1.  
Expected size: one day.

### 3. Isolate protocols, updater, telemetry, and support URLs

Goal: prevent the fork from impersonating or calling GitHub Desktop services.

Scope:

- Replace GitHub Desktop protocol handlers with WinGit-owned schemes.
- Disable production update checks until WinGit has an update service.
- Disable GitHub Desktop telemetry endpoints by default.
- Replace release notes, support, acknowledgements, and issue links.
- Replace GitHub-hosted package artwork.

Acceptance criteria:

- Startup registers no `x-github-*` or `github-windows` handler for WinGit.
- A development and packaged build sends no requests to
  `desktop.github.com` or `desktop.githubusercontent.com` during startup.
- Errors direct users to a WinGit-owned issue location.

Verification: run an application startup smoke test with request logging and
assert that no GitHub Desktop service hostname appears.  
Dependencies: 2.  
Expected size: two days.

### 4. Add provider-independent commit-message types

Goal: separate the application feature from the Copilot SDK.

Scope:

- Add the `CommitMessageGenerator` contract and a provider-neutral result type.
- Move provider-independent prompt and parser behavior behind that contract.
- Keep `AppStore` behavior unchanged through a temporary Copilot adapter.

Acceptance criteria:

- `AppStore` no longer imports Copilot SDK types for commit generation.
- Existing commit generation behavior passes through the new contract.
- No user-visible behavior changes.

Verification: run the existing commit-message, store, and warning-dialog unit
tests through `yarn test:unit` with their exact file paths.  
Dependencies: 1.  
Expected size: one day.

### 5. Prove and pin the Codex runtime package

Goal: choose a supported Codex runtime that WinGit can package.

Scope:

- Pin one official Codex package and version.
- Record its license, transitive license obligations, Windows architectures,
  installed size, binary resolution, and update method.
- Prove that it can start `app-server` from a packaged-like directory.
- Record whether OpenAI permits redistribution and third-party managed OAuth.

Acceptance criteria:

- A script starts the pinned runtime and completes `initialize` and
  `account/read` on Windows.
- License generation includes the runtime and its transitive dependencies.
- Redistribution and managed OAuth are either confirmed with primary evidence
  or marked as public-release blockers.

Verification: run the probe from a temporary directory that contains only the
files intended for packaging.  
Dependencies: 1.  
Expected size: two days.

### 6. Add the App Server process supervisor

Goal: give Electron's main process reliable ownership of Codex App Server.

Scope:

- Start one pinned App Server child process on demand.
- Set the app-specific Codex data directory.
- Capture stderr with secret redaction.
- Add startup timeout, unexpected-exit reporting, restart limits, and shutdown.

Acceptance criteria:

- Concurrent renderer requests share one healthy process.
- App shutdown leaves no Codex child process running.
- Three repeated startup failures stop automatic retries and return one clear
  error to the renderer.

Verification: add process-supervisor tests with a controllable fake process,
then run a real start and shutdown probe.  
Dependencies: 5.  
Expected size: two days.

### 7. Add typed JSON-RPC transport and IPC

Goal: connect the renderer to App Server without exposing secrets or process
handles.

Scope:

- Implement JSON line framing, request IDs, responses, notifications, server
  requests, timeouts, and cancellation.
- Expose a narrow typed IPC API for account and generation operations.
- Reject unexpected renderer origins and unrecognized methods.

Acceptance criteria:

- Multiple in-flight requests resolve to the correct callers.
- Malformed output fails one request without corrupting later messages.
- Renderer state contains no OAuth token or child-process handle.

Verification: run transport tests covering interleaved responses, malformed
JSON, EOF, timeout, notification delivery, and shutdown.  
Dependencies: 6.  
Expected size: two days.

### 8. Add Codex account state and managed sign-in

Goal: let users connect their ChatGPT subscription independently of GitHub
account state.

Scope:

- Implement `account/read`, browser login, device-code login, completion,
  cancellation, logout, and account-update notifications.
- Model signed-out, signing-in, signed-in, expired, rate-limited, and error
  states.
- Keep OAuth URLs and tokens out of logs and application persistence.

Acceptance criteria:

- A user without a GitHub account can sign into Codex.
- Restarting WinGit restores the App Server-managed login.
- Logout removes the Codex account without signing out of GitHub.
- Password, passkey, and MFA steps remain in the system browser.

Verification: exercise browser and device-code login with a test account, then
restart and logout. Inspect logs for credential material.  
Dependencies: 7.  
Expected size: two days.

### 9. Replace Copilot entitlement UI with Codex settings

Goal: make Codex access understandable in Settings.

Scope:

- Rename the user-facing settings area to **Codex**.
- Show sign-in, account, plan, sign-out, and error states.
- Remove Copilot plan purchase and organization-policy messages from the Codex
  path.
- Hide model selection until a supported subscription model list is available.

Acceptance criteria:

- Settings never require a Copilot license for Codex.
- Each account state has one clear action.
- Keyboard focus and screen-reader names work for every action.

Verification: add component tests for every account state and perform a Windows
keyboard-only pass.  
Dependencies: 8.  
Expected size: one and a half days.

### 10. Show subscription rate-limit state

Goal: explain when generation is available without inventing API billing data.

Scope:

- Read App Server rate limits and subscribe to updates.
- Show available, near-limit, exhausted, reset-time, and unavailable states.
- Keep usage display separate from GitHub accounts and Copilot quotas.

Acceptance criteria:

- An exhausted limit disables generation and shows the reported reset time.
- Missing or changed rate-limit fields degrade to "Unavailable" rather than a
  fabricated percentage.
- No UI labels imply API charges.

Verification: run unit tests with representative and unknown rate-limit payloads
and inspect the Settings view.  
Dependencies: 8 and 9.  
Expected size: one day.

### 11. Build the bounded Codex commit request

Goal: send only the selected commit context to Codex.

Scope:

- Reuse the selected-file diff from `AppStore._generateCommitMessage`.
- Reuse sanitized repository commit rules and randomized trust-boundary tags.
- Create a fresh bounded thread with no tools and a read-only sandbox.
- Request a structured title and description.

Acceptance criteria:

- Unselected file contents do not appear in the request.
- The request exposes no shell, patch, network, MCP, plugin, or write tool.
- A repository instruction inside the diff cannot escape the randomized diff
  block.
- Each generation starts without context from the previous repository.

Verification: use a fake App Server to capture the exact request and assert its
context, tool, sandbox, and persistence fields.  
Dependencies: 4 and 7.  
Expected size: one and a half days.

### 12. Parse results, cancel turns, and map errors

Goal: make Codex generation behave like a native part of the existing commit
form.

Scope:

- Validate the structured result and existing title limits.
- Connect the current Stop action to App Server interruption.
- Map auth, rate limit, timeout, runtime exit, invalid output, and cancellation
  to distinct application outcomes.
- Preserve the user's existing commit text when generation fails.

Acceptance criteria:

- Valid output fills the editable Summary and Description fields.
- Invalid output never clears existing text.
- Cancellation stops the loading state without an error dialog.
- Retrying after a recoverable failure starts a new request.

Verification: run store tests for every mapped outcome and a real cancellation
probe against App Server.  
Dependencies: 10 and 11.  
Expected size: one and a half days.

### 13. Enable generation without GitHub or Copilot entitlement

Goal: make the feature depend only on local changes and Codex account state.

Scope:

- Replace `getAccountForCommitMessageGeneration` for the Codex path.
- Show the generation action for local-only, GitHub.com, and GitHub Enterprise
  repositories when Codex is ready.
- Keep the button disabled for no selected changes, active generation, or an
  exhausted subscription limit.

Acceptance criteria:

- A local repository with no remote can generate a message.
- Signing out of GitHub does not sign out of Codex or hide generation.
- Signing out of Codex disables generation without affecting Git operations.

Verification: add unit tests for the repository and account-state matrix, then
exercise a local repository in the running app.  
Dependencies: 9, 10, and 12.  
Expected size: one day.

### 14. Add privacy consent and safe diagnostics

Goal: make repository data transfer explicit and keep sensitive content out of
logs.

Scope:

- Replace the Copilot disclaimer with a Codex-specific disclosure.
- State exactly which selected data goes to OpenAI.
- Add per-repository acknowledgement with a clear reset path.
- Redact prompts, diffs, OAuth values, and generated text from diagnostics.

Acceptance criteria:

- The first request for a repository waits for consent.
- Declining consent sends no App Server turn.
- A diagnostic bundle contains status and timing but no repository content or
  credentials.

Verification: run consent component tests and scan a generated diagnostic
bundle after success, failure, and login.  
Dependencies: 13.  
Expected size: one day.

### 15. Add a commit-generation integration test

Goal: prove the full application path without contacting a live model.

Scope:

- Add a deterministic fake App Server process.
- Drive selected changes through UI, dispatcher, IPC, App Server transport, and
  commit form update.
- Cover generation, cancellation, invalid output, and rate limit exhaustion.

Acceptance criteria:

- The test fails if the UI bypasses the main-process service.
- The test proves that only selected changes reach the fake server.
- The test proves that no Git commit occurs.

Verification: run the new integration test twice from a clean checkout and
confirm identical results.  
Dependencies: 14.  
Expected size: two days.

### 16. Remove the Copilot runtime and commit-generation path

Goal: stop shipping GitHub Copilot code for the completed feature.

Scope:

- Remove the Copilot SDK dependency from commit generation.
- Remove GitHub Copilot model, quota, BYOK, and entitlement state that no longer
  has a caller outside conflict resolution.
- Update license generation and bundled files.
- Keep conflict-only code until Milestone M3 replaces it.

Acceptance criteria:

- Commit generation has no `@github/copilot-sdk` runtime dependency.
- The production bundle contains no Copilot CLI files used only by commit
  generation.
- Existing non-AI Git workflows remain unchanged.

Verification: inspect the production bundle, run targeted unit tests, and run
`yarn build:dev`.  
Dependencies: 15.  
Expected size: two days.

### 17. Package the Codex runtime for Windows

Goal: make the commit MVP installable on a clean Windows account.

Scope:

- Include the pinned runtime and license notices in Squirrel packaging.
- Support x64 first and report unsupported architecture before sign-in.
- Set executable permissions and runtime paths without relying on global Codex.
- Keep GitHub Desktop installed during the test.

Acceptance criteria:

- The installer works without Node.js, Codex CLI, or developer tools installed.
- WinGit starts App Server and reaches signed-out Codex settings.
- WinGit and GitHub Desktop coexist without shortcut, protocol, or data-dir
  collisions.

Verification: install on a clean Windows VM and record installer, first launch,
sign-in page, generation, uninstall, and GitHub Desktop coexistence results.  
Dependencies: 3, 5, and 16.  
Expected size: two days.

### 18. Run the commit MVP release gate

Goal: decide whether the build is ready for a limited preview.

Scope:

- Run targeted tests, the full unit suite, lint, development build, production
  package, and clean-VM smoke test.
- Confirm redistribution, OAuth, privacy, and trademark gates.
- Record installer size, startup time, generation latency, and known limits.

Acceptance criteria:

- Every required check has a pass, fail, or explicit external block.
- No failed check is described as passing because another platform passed.
- The preview remains private if redistribution or third-party OAuth is not
  confirmed.

Verification: attach the commands, logs, installer digest, and observed UI
results to the issue.  
Dependencies: 17.  
Expected size: one day.

### 19. Add a provider-independent conflict contract

Goal: separate conflict suggestions from the Copilot SDK.

Scope:

- Define structured conflict input, progress, file suggestion, skipped-file,
  and summary types.
- Adapt current conflict chunking and reassembly behind the contract.
- Preserve current size and binary exclusions.

Acceptance criteria:

- Conflict UI and `AppStore` no longer import Copilot SDK types.
- Existing conflict model and reassembly tests pass through the new contract.
- No application path writes a suggestion automatically.

Verification: run all current conflict context, model, resolution, helper, and
UI unit tests.  
Dependencies: 4 and 18.  
Expected size: two days.

### 20. Generate Codex conflict suggestions

Goal: produce structured suggestions with the same safety limits as the current
feature.

Scope:

- Send conflict labels, refs, marker context, and supported text files.
- Use no write-capable tool.
- Validate paths and suggestion content before returning it to the renderer.
- Report partial failures without discarding valid suggestions.

Acceptance criteria:

- Binary, unreadable, oversized, and malformed conflicts are skipped with a
  reason.
- Returned paths must match an input conflicted path.
- A malformed suggestion cannot write or replace a file.

Verification: run fake-server tests with valid, partial, malicious, and
malformed responses.  
Dependencies: 19.  
Expected size: two days.

### 21. Connect conflict progress and cancellation

Goal: preserve responsive conflict UX during longer Codex turns.

Scope:

- Map App Server events to existing progress stages.
- Connect Stop to turn interruption and local cleanup.
- Handle App Server exit and rate-limit exhaustion during multi-chunk work.

Acceptance criteria:

- Progress never exposes private reasoning text.
- Stop prevents remaining chunks from starting.
- A stopped or failed request leaves conflict files untouched.

Verification: run deterministic event-sequence tests and cancel a real request
mid-turn.  
Dependencies: 20.  
Expected size: one day.

### 22. Rebrand and verify the conflict review flow

Goal: let users review Codex suggestions and apply them intentionally.

Scope:

- Replace user-facing Copilot labels, disclaimers, callouts, and errors.
- Keep side-by-side review, skipped-file summary, manual fallback, and explicit
  apply controls.
- Remove the remaining Copilot SDK, bundled CLI, settings, and license entries.

Acceptance criteria:

- No user-facing Copilot text remains in production conflict flows.
- Applying one suggestion changes only the reviewed conflicted file.
- Rejecting or closing the dialog changes no file.
- The production bundle contains no `@github/copilot-sdk` or Copilot CLI.

Verification: run conflict unit tests, inspect the production bundle, and
exercise accept, reject, cancel, and partial failure in the packaged app.  
Dependencies: 21.  
Expected size: two days.

### 23. Add an owned update and release channel

Goal: update WinGit without contacting or modifying GitHub Desktop.

Scope:

- Create WinGit release metadata and update endpoints.
- Use WinGit application IDs, signing identity, release notes, and installer
  names.
- Add staged rollout and rollback instructions.
- Keep automatic updates off until signed update verification passes.

Acceptance criteria:

- A signed older WinGit build updates to a signed newer WinGit build.
- A tampered update is rejected.
- GitHub Desktop's version, files, shortcuts, and updater remain unchanged.

Verification: perform an old-to-new update in a clean VM and compare installed
file digests and signatures.  
Dependencies: 18 and 22.  
Expected size: two days.

### 24. Finish product, privacy, and contributor documentation

Goal: make the fork's identity, data handling, and development workflow clear.

Scope:

- Complete the trademark and package-name check for the release name.
- Replace remaining GitHub Desktop product artwork and copy.
- Publish a privacy description for GitHub and OpenAI data paths.
- Document local build, Codex runtime update, tests, packaging, and upstream
  sync.

Acceptance criteria:

- A repository-wide search finds no production GitHub Desktop name or logo
  except required attribution and historical references.
- Documentation distinguishes GitHub authentication from ChatGPT sign-in.
- A new contributor can build and run the Codex settings screen from a clean
  checkout.

Verification: run the documented setup on a clean Windows account and audit the
packaged UI and files for protected branding.  
Dependencies: 23.  
Expected size: two days.

### 25. Run the public-release and upstream-sync gate

Goal: prove the first public release and maintenance path.

Scope:

- Fetch current upstream and perform a rehearsal sync in an isolated branch.
- Run full unit, lint, build, package, update, auth, commit, conflict, privacy,
  and coexistence checks.
- Verify installer and update signatures and publish their digests.
- Record unresolved upstream conflicts and release limitations.

Acceptance criteria:

- The fork syncs with current upstream without dropping Codex or Git behavior.
- Both commit generation and conflict suggestion flows pass in the packaged app.
- All external blockers are closed before public distribution.
- The release checklist can be repeated without undocumented machine state.

Verification: execute the release checklist from a fresh clone and a clean VM,
then independently download the published installer and verify its digest and
signature.  
Dependencies: 24.  
Expected size: two days.

## Dependency order

The first usable result follows this path:

```text
1 -> 2 -> 3
1 -> 4 -> 11 -> 12 -> 13 -> 14 -> 15 -> 16
1 -> 5 -> 6 -> 7 -> 8 -> 9 -> 10 -> 11
3 + 5 + 16 -> 17 -> 18
```

Conflict suggestions follow the preview gate:

```text
18 -> 19 -> 20 -> 21 -> 22
18 + 22 -> 23 -> 24 -> 25
```

## Risks and closing evidence

| Risk | Likelihood | Cost | Close by | Required evidence |
| --- | --- | --- | --- | --- |
| Codex runtime redistribution or third-party OAuth is not permitted | Medium until confirmed | Blocks public distribution | Issue 5 | Current OpenAI license, terms, and written product guidance that covers the selected integration. |
| App Server protocol changes break the packaged client | Medium | AI features fail after runtime update | Issue 7 | Pin the runtime, generate or validate message types, and run compatibility tests before upgrades. |
| WinGit inherits the user's Codex configuration | Medium | Plugins or instructions change results or gain unwanted capabilities | Issue 6 | A process probe proves the app-specific data directory and disabled tool set. |
| A diff injects model instructions | Medium | Misleading output or unintended context use | Issue 11 | Captured request tests and malicious-diff fixtures prove the trust boundary. |
| Sensitive repository data appears in logs | Medium | Private source disclosure | Issue 14 | Diagnostic bundle scans after login, success, cancellation, and failure. |
| Rebranding collides with GitHub Desktop | High without work | Broken shortcuts, auth callbacks, updates, or user data | Issues 2, 3, and 17 | Side-by-side packaged install and uninstall on a clean VM. |
| Upstream changes repeatedly conflict with the AI integration | Medium | Maintenance delays and dropped fixes | Issue 25 | A rehearsal upstream sync with measured conflicts and a documented resolution path. |

The change is safe only if the provider boundary stays narrow and the packaged
coexistence test proves that WinGit does not own GitHub Desktop's application
identity or data. That fact is currently unproven and remains a release gate.

## Definition of done

### Each issue

An issue is done when:

- Its acceptance criteria pass against the real source or running artifact.
- Its narrow automated tests pass.
- Changed user behavior has an observed UI result.
- Logs and test fixtures contain no credentials or private repository content.
- The pull request contains no unrelated reformatting or upstream sync work.

### Each milestone

A milestone is done when every included issue is done and its exit result is
verified in the running application. A successful unit test does not replace a
required packaged-app observation.

### First public release

The first public release is done only when:

- OpenAI redistribution and managed OAuth gates are confirmed.
- GitHub trademark and product-identity work is complete.
- A clean Windows machine can install WinGit beside GitHub Desktop.
- A ChatGPT subscriber can sign in, generate an editable commit message from
  selected changes, restart, sign out, and uninstall.
- The installer and update packages have verified signatures and published
  digests.
- The independent download check matches the released source and commit.

## Start here

Start with **Issue 1: Record the fork baseline and upstream policy**. It creates
the stable reference needed for every later issue and makes the first upstream
sync reviewable.
