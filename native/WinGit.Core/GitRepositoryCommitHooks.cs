namespace WinGit.Core;

/// <summary>
/// A commit or amend attempt that Git rejected after running repository hooks.
/// Unlike Electron's hook proxy, the native path runs <c>git commit</c>
/// directly, so Git does not name the failing hook: hook output is the only
/// diagnostic. The exception carries the operation, the detected hook name
/// when the output names one, the installed commit hooks for context, and the
/// actual HEAD observed after the failure so callers never assume HEAD or
/// files are unchanged. There is intentionally no skip-hooks option on this
/// path; a retry runs hooks again.
/// </summary>
public sealed class CommitHookFailureException : InvalidOperationException
{
    public CommitHookFailureException(
        string message,
        string? hookName,
        string operation,
        string diagnosticOutput,
        string? expectedHeadId,
        string? actualHeadId,
        int exitCode,
        IReadOnlyList<string> installedHooks)
        : base(message)
    {
        HookName = hookName;
        Operation = operation;
        DiagnosticOutput = diagnosticOutput;
        ExpectedHeadId = expectedHeadId;
        ActualHeadId = actualHeadId;
        ExitCode = exitCode;
        InstalledHooks = installedHooks;
    }

    /// <summary>A known commit hook name when the diagnostic output names one; otherwise null.</summary>
    public string? HookName { get; }

    /// <summary>"commit" or "amend".</summary>
    public string Operation { get; }

    /// <summary>Sanitized, bounded hook output from Git's stderr.</summary>
    public string DiagnosticOutput { get; }

    public string? ExpectedHeadId { get; }

    /// <summary>HEAD observed after the failure; null when it could not be read.</summary>
    public string? ActualHeadId { get; }

    public int ExitCode { get; }

    public IReadOnlyList<string> InstalledHooks { get; }

    /// <summary>True when HEAD still matches the expected value, so no commit was created.</summary>
    public bool HeadUnchanged =>
        ExpectedHeadId is not null
        && ActualHeadId is not null
        && string.Equals(ExpectedHeadId, ActualHeadId, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class GitRepositoryService
{
    private static readonly string[] CommitHookNames =
    [
        "pre-commit",
        "prepare-commit-msg",
        "commit-msg",
        "post-commit",
        "post-rewrite",
        "pre-auto-gc",
    ];

    private const int MaximumHookDiagnosticChars = 2_000;

    /// <summary>
    /// Runs <c>git commit</c> with hooks enabled and maps a hook rejection to a
    /// <see cref="CommitHookFailureException"/> that records the actual HEAD.
    /// Non-hook failures keep their original <see cref="GitCommandException"/>.
    /// </summary>
    internal async Task<string> RunCommitWithHookHandlingAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        string? standardInput,
        string? expectedHeadId,
        bool amend,
        CancellationToken cancellationToken)
    {
        GitCommandException? failure = null;
        try
        {
            await processRunner.RunAsync(
                repositoryRoot,
                arguments,
                cancellationToken,
                standardInput: standardInput).ConfigureAwait(false);
        }
        catch (GitCommandException exception) when (!cancellationToken.IsCancellationRequested)
        {
            failure = exception;
        }

        if (failure is null)
        {
            var head = await processRunner.RunAsync(
                repositoryRoot,
                ["rev-parse", "HEAD"],
                cancellationToken).ConfigureAwait(false);
            EnsureComplete(head, "commit identity");
            var commitId = DecodeUtf8(head.StandardOutput, "commit identity").Trim();
            ValidateCommitId(commitId);
            return commitId;
        }

        var operation = amend ? "amend" : "commit";
        var detectedHook = TryDetectCommitHookName(failure.StandardError);
        var installedHooks = await ListInstalledCommitHooksAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        if (detectedHook is null && installedHooks.Count == 0)
        {
            throw failure;
        }

        var actualHeadId = await ReadHeadIdBestEffortAsync(
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        throw BuildCommitHookFailure(
            failure,
            operation,
            detectedHook,
            installedHooks,
            expectedHeadId,
            actualHeadId);
    }

    private static string? TryDetectCommitHookName(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return null;
        }

        foreach (var hookName in CommitHookNames)
        {
            if (standardError.Contains(hookName, StringComparison.OrdinalIgnoreCase))
            {
                return hookName;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> ListInstalledCommitHooksAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var hooksPathResult = await processRunner.RunAsync(
                repositoryRoot,
                ["rev-parse", "--git-path", "hooks"],
                cancellationToken).ConfigureAwait(false);
            var hooksPathText = DecodeUtf8(hooksPathResult.StandardOutput, "hooks path").Trim();
            if (string.IsNullOrWhiteSpace(hooksPathText))
            {
                return [];
            }

            var hooksPath = Path.IsPathRooted(hooksPathText)
                ? Path.GetFullPath(hooksPathText)
                : Path.GetFullPath(Path.Combine(repositoryRoot, hooksPathText));
            if (!Directory.Exists(hooksPath))
            {
                return [];
            }

            var installed = new List<string>();
            foreach (var hookName in CommitHookNames)
            {
                foreach (var candidate in Directory.EnumerateFiles(hooksPath, hookName + "*"))
                {
                    var fileName = Path.GetFileName(candidate);
                    if (fileName.EndsWith(".sample", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var stem = fileName.Contains('.')
                        ? fileName[..fileName.IndexOf('.')]
                        : fileName;
                    if (string.Equals(stem, hookName, StringComparison.OrdinalIgnoreCase))
                    {
                        installed.Add(hookName);
                        break;
                    }
                }
            }

            return installed.AsReadOnly();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<string?> ReadHeadIdBestEffortAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var head = await processRunner.RunAsync(
                repositoryRoot,
                ["rev-parse", "HEAD"],
                cancellationToken,
                expectedExitCodes: [128]).ConfigureAwait(false);
            if (head.ExitCode != 0)
            {
                return null;
            }

            var commitId = DecodeUtf8(head.StandardOutput, "commit identity").Trim();
            return CommitIdPattern.IsMatch(commitId) ? commitId : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static CommitHookFailureException BuildCommitHookFailure(
        GitCommandException failure,
        string operation,
        string? hookName,
        IReadOnlyList<string> installedHooks,
        string? expectedHeadId,
        string? actualHeadId)
    {
        var diagnostic = failure.StandardError.Trim();
        if (diagnostic.Length > MaximumHookDiagnosticChars)
        {
            diagnostic = diagnostic[..MaximumHookDiagnosticChars] + "…";
        }

        if (diagnostic.Length == 0)
        {
            diagnostic = "Git reported no hook output.";
        }

        var subject = hookName is not null
            ? $"The {hookName} hook failed"
            : installedHooks.Count > 0
                ? $"A commit hook failed ({string.Join(", ", installedHooks)} installed)"
                : "A commit hook failed";
        var verb = operation == "amend" ? "amending the commit" : "creating the commit";

        string headState;
        if (expectedHeadId is not null && actualHeadId is not null)
        {
            headState = string.Equals(expectedHeadId, actualHeadId, StringComparison.OrdinalIgnoreCase)
                ? $" No commit was created; HEAD is still {ShortId(actualHeadId)}."
                : $" HEAD is now {ShortId(actualHeadId)}, so inspect the repository before retrying.";
        }
        else if (actualHeadId is not null)
        {
            headState = $" HEAD is {ShortId(actualHeadId)}.";
        }
        else
        {
            headState = " The repository state was refreshed; inspect History before retrying.";
        }

        var message = $"{subject} while {verb}.{headState} Fix the problem, then try again — the draft and selection were kept. Hook output: {diagnostic}";
        return new CommitHookFailureException(
            message,
            hookName,
            operation,
            diagnostic,
            expectedHeadId,
            actualHeadId,
            failure.ExitCode,
            installedHooks);
    }

    private static string ShortId(string commitId) =>
        commitId.Length > 7 ? commitId[..7] : commitId;
}
