# WinGit privacy description

WinGit has two independent account paths. Signing out of one does not sign out
of the other.

## GitHub account path

GitHub sign-in is used for repository hosting features such as cloning private
repositories, fetching, pushing, pull requests, issues, checks, organization
access, and GitHub notifications. WinGit sends the repository and account data
needed for the action to GitHub's APIs and Git endpoints. GitHub credentials are
stored through the operating system credential store. Git operations may also
contact any remotes configured by the user, including non-GitHub hosts.

## ChatGPT and Codex path

Codex sign-in is managed by the bundled OpenAI Codex App Server. WinGit opens
the Codex-provided browser flow and never asks the user to paste a ChatGPT token
or API key. Codex stores its authentication state in a WinGit-specific data
directory.

WinGit sends content to OpenAI only after the user requests an AI action and,
on first use, accepts the disclosure:

- commit generation sends the selected textual diff plus bounded repository
  context needed to draft a message;
- conflict suggestions send supported text conflicts, conflict labels, refs,
  and bounded commit or pull-request context;
- binary, unreadable, oversized, unselected, and excluded files are not sent.

Generated commit messages remain editable. Conflict suggestions are review-only
until the user continues, and a manual per-file choice rejects the generated
write for that path. Stop interrupts the active Codex turn and prevents later
chunks from starting.

WinGit does not expose model reasoning. Diagnostics record sanitized status,
duration, size, and failure categories without repository paths, prompt text,
diff content, generated text, tokens, or account identifiers.

## Product services and updates

Usage telemetry and inherited GitHub Desktop product-service endpoints are
disabled. WinGit has no production update URL by default. An owned HTTPS feed
and a separate automatic-update release gate must be configured at build time.

## Local data and deletion

WinGit stores application preferences, repository lists, GitHub credentials,
and Codex state locally. Removing a repository from WinGit does not delete its
working directory unless a separate destructive action explicitly says so.
Signing out of GitHub removes the GitHub account from WinGit; signing out of
Codex asks App Server to remove its ChatGPT session without changing GitHub.

Before public distribution, the project still requires explicit confirmation
that third-party ChatGPT subscription sign-in is permitted for this client. See
the [preview release gate](process/win-git-preview-release-gate.md).
