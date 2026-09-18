namespace WinGit.Native;

internal sealed record NativeCaptureOptions(
    string OutputPath,
    string View,
    string? RepositoryPath,
    string? Theme,
    string? ComparisonReference)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out NativeCaptureOptions? options,
        out string? error)
    {
        options = null;
        error = null;
        var captureRequested = false;
        string? outputPath = null;
        string? view = null;
        string? repositoryPath = null;
        string? positionalRepository = null;
        string? theme = null;
        string? comparisonReference = null;

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument.ToLowerInvariant())
            {
                case "--capture":
                    captureRequested = true;
                    if (!TryReadValue(arguments, ref index, out outputPath, out error, "--capture"))
                    {
                        return false;
                    }

                    break;
                case "--view":
                    if (!TryReadValue(arguments, ref index, out view, out error, "--view"))
                    {
                        return false;
                    }

                    break;
                case "--repository":
                    if (!TryReadValue(arguments, ref index, out repositoryPath, out error, "--repository"))
                    {
                        return false;
                    }

                    break;
                case "--theme":
                    if (!TryReadValue(arguments, ref index, out theme, out error, "--theme"))
                    {
                        return false;
                    }

                    break;
                case "--compare-branch":
                    if (!TryReadValue(arguments, ref index, out comparisonReference, out error, "--compare-branch"))
                    {
                        return false;
                    }

                    break;
                default:
                    if (!argument.StartsWith("--", StringComparison.Ordinal) && positionalRepository is null)
                    {
                        positionalRepository = argument.Trim('"');
                    }

                    break;
            }
        }

        // Diagnostic options are deliberately inert unless --capture is present.
        // This keeps existing positional repository launches backward compatible.
        if (!captureRequested)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error = "--capture requires an absolute PNG output path.";
            return false;
        }

        outputPath = outputPath.Trim('"');
        if (!Path.IsPathFullyQualified(outputPath)
            || !string.Equals(Path.GetExtension(outputPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            error = "--capture requires an absolute path ending in .png.";
            return false;
        }

        view = string.IsNullOrWhiteSpace(view) ? "changes" : view.ToLowerInvariant();
        if (view is not ("changes" or "history" or "settings" or "branches" or "worktrees" or "stashes" or "remotes" or "tags" or "repository-open-check" or "repository-picker-check" or "history-selection-check" or "history-comparison-check" or "whole-file-staging-check" or "partial-staging-check" or "partial-discard-check" or "commit-composer-check" or "whole-file-discard-check"))
        {
            error = "--view must be changes, history, settings, branches, worktrees, stashes, remotes, tags, repository-open-check, repository-picker-check, history-selection-check, history-comparison-check, whole-file-staging-check, partial-staging-check, partial-discard-check, commit-composer-check, or whole-file-discard-check.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(comparisonReference) && view != "history")
        {
            error = "--compare-branch can only be used with --view history.";
            return false;
        }

        comparisonReference = string.IsNullOrWhiteSpace(comparisonReference)
            ? null
            : comparisonReference.Trim();

        theme = string.IsNullOrWhiteSpace(theme) ? null : theme.ToLowerInvariant();
        if (theme is not (null or "light" or "dark"))
        {
            error = "--theme must be light or dark.";
            return false;
        }

        var selectedRepository = string.IsNullOrWhiteSpace(repositoryPath)
            ? positionalRepository
            : repositoryPath;
        if (!string.IsNullOrWhiteSpace(selectedRepository))
        {
            selectedRepository = selectedRepository.Trim('"');
            try
            {
                selectedRepository = Path.GetFullPath(selectedRepository);
            }
            catch (Exception)
            {
                error = "The repository path is invalid.";
                return false;
            }
        }

        options = new NativeCaptureOptions(outputPath, view, selectedRepository, theme, comparisonReference);
        return true;
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        out string? value,
        out string? error,
        string option)
    {
        value = null;
        error = null;
        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = $"{option} requires a value.";
            return false;
        }

        value = arguments[++index];
        return true;
    }
}
