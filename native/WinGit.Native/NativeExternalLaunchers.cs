using System.ComponentModel;
using System.Diagnostics;

namespace WinGit.Native;

internal sealed class NativeExternalLaunchException : InvalidOperationException
{
    public NativeExternalLaunchException(string message)
        : base(message)
    {
    }

    public NativeExternalLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Launches a discovered editor or shell without going through cmd.exe or a
/// shell-interpolated command string. Every path comes from a discovered
/// immutable option or from a repository path validated by this class.
/// </summary>
internal static class NativeExternalLaunchers
{
    public static void LaunchEditor(
        NativeIntegrationOption editor,
        string repositoryRoot,
        string? relativePath = null)
    {
        ValidateIntegration(editor, NativeIntegrationKind.Editor);
        var resolved = ResolveRepositoryPath(repositoryRoot, relativePath, allowMissingTarget: false);
        StartProcess(
            editor.ExecutablePath,
            [resolved.TargetPath],
            resolved.RepositoryRoot,
            editor.DisplayName);
    }

    public static void LaunchShell(
        NativeIntegrationOption shell,
        string repositoryRoot)
    {
        ValidateIntegration(shell, NativeIntegrationKind.Shell);
        if (shell.ShellKind is not NativeShellKind shellKind)
        {
            throw new NativeExternalLaunchException(
                $"The selected shell '{shell.DisplayName}' has no launch type.");
        }

        var resolved = ResolveRepositoryPath(repositoryRoot, null, allowMissingTarget: false);
        StartProcess(
            shell.ExecutablePath,
            GetShellArguments(shellKind, resolved.RepositoryRoot),
            resolved.RepositoryRoot,
            shell.DisplayName);
    }

    public static void RevealInFileManager(
        string repositoryRoot,
        string? relativePath = null)
    {
        var resolved = ResolveRepositoryPath(repositoryRoot, relativePath, allowMissingTarget: true);
        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        if (!File.Exists(explorerPath))
        {
            throw new NativeExternalLaunchException(
                "Windows File Explorer is unavailable on this system.");
        }

        if (resolved.UsedExistingParentForMissingTarget)
        {
            StartProcess(
                explorerPath,
                [resolved.TargetPath],
                resolved.RepositoryRoot,
                "File Explorer");
            return;
        }

        // Explorer's /select switch is a legacy command-line grammar. It
        // expects the switch and a quoted path in one raw command line; an
        // ArgumentList item quotes the whole `/select,<path>` token and
        // Explorer then falls back to its default folder.
        StartProcessWithCommandLine(
            explorerPath,
            BuildExplorerSelectArguments(resolved.TargetPath),
            resolved.RepositoryRoot,
            "File Explorer");
    }

    private static void ValidateIntegration(
        NativeIntegrationOption integration,
        NativeIntegrationKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(integration);
        if (integration.Kind != expectedKind)
        {
            throw new NativeExternalLaunchException(
                $"The selected integration '{integration.DisplayName}' is not a {expectedKind.ToString().ToLowerInvariant()}.");
        }

        if (!integration.StableId.StartsWith(
                expectedKind == NativeIntegrationKind.Editor ? "editor." : "shell.",
                StringComparison.Ordinal)
            || !TryGetExecutablePath(integration.ExecutablePath, out _))
        {
            throw new NativeExternalLaunchException(
                $"The selected {expectedKind.ToString().ToLowerInvariant()} is no longer available. Open Settings and choose an installed tool.");
        }
    }

    private static string[] GetShellArguments(NativeShellKind shellKind, string repositoryRoot) =>
        shellKind switch
        {
            NativeShellKind.CommandPrompt => [],
            NativeShellKind.PowerShell => [],
            NativeShellKind.PowerShellCore => [],
            NativeShellKind.Hyper => [repositoryRoot],
            NativeShellKind.GitBash => [$"--cd={repositoryRoot}"],
            // Mintty's --dir accepts the Windows working directory directly,
            // and '-' starts its login shell without a shell-interpolated cd.
            NativeShellKind.Cygwin => ["--dir", repositoryRoot, "-"],
            NativeShellKind.Wsl => [],
            NativeShellKind.WindowsTerminal => ["-d", repositoryRoot],
            NativeShellKind.FluentTerminal => ["new"],
            NativeShellKind.Alacritty => ["--working-directory", repositoryRoot],
            NativeShellKind.Warp =>
                [$"warp://action/new_tab?path={Uri.EscapeDataString(repositoryRoot)}"],
            _ => throw new NativeExternalLaunchException(
                $"The selected shell type '{shellKind}' is not supported."),
        };

    private static string BuildExplorerSelectArguments(string targetPath)
    {
        if (targetPath.Contains('"'))
        {
            throw new NativeExternalLaunchException(
                "The selected path cannot be opened because it contains an invalid quote character.");
        }

        // A trailing backslash would escape the closing quote in a Windows
        // command line, so double it before adding the surrounding quotes.
        var quotedPath = targetPath.EndsWith("\\", StringComparison.Ordinal)
            ? $"{targetPath}\\"
            : targetPath;
        return $"/select,\"{quotedPath}\"";
    }

    private static void StartProcess(
        string executablePath,
        IEnumerable<string> arguments,
        string workingDirectory,
        string displayName)
    {
        var startInfo = CreateStartInfo(executablePath, workingDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        StartProcess(startInfo, displayName);
    }

    private static void StartProcessWithCommandLine(
        string executablePath,
        string commandLineArguments,
        string workingDirectory,
        string displayName)
    {
        var startInfo = CreateStartInfo(executablePath, workingDirectory);
        startInfo.Arguments = commandLineArguments;
        StartProcess(startInfo, displayName);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string workingDirectory) =>
        new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = false,
        };

    private static void StartProcess(ProcessStartInfo startInfo, string displayName)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new NativeExternalLaunchException(
                    $"Windows could not start {displayName}.");
            }
        }
        catch (NativeExternalLaunchException)
        {
            throw;
        }
        catch (Win32Exception exception)
        {
            throw new NativeExternalLaunchException(
                $"Unable to start {displayName}. Check its installation in Settings.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new NativeExternalLaunchException(
                $"Unable to start {displayName}. Check its installation in Settings.",
                exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new NativeExternalLaunchException(
                $"Unable to start {displayName}. Check its installation in Settings.",
                exception);
        }
    }

    private static ResolvedRepositoryPath ResolveRepositoryPath(
        string repositoryRoot,
        string? relativePath,
        bool allowMissingTarget)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)
            || repositoryRoot.Contains('\0'))
        {
            throw new NativeExternalLaunchException(
                "The repository path is invalid or unavailable.");
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(repositoryRoot);
        }
        catch (ArgumentException exception)
        {
            throw new NativeExternalLaunchException(
                "The repository path is invalid or unavailable.",
                exception);
        }

        if (!Directory.Exists(normalizedRoot))
        {
            throw new NativeExternalLaunchException(
                "The repository directory is no longer available.");
        }

        EnsureNoReparsePoint(normalizedRoot, "The repository directory cannot be verified safely.");

        var relative = relativePath ?? string.Empty;
        if (relative.Contains('\0') || Path.IsPathRooted(relative))
        {
            throw new NativeExternalLaunchException(
                "The selected path must be relative to the repository.");
        }

        string requestedPath;
        try
        {
            requestedPath = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        }
        catch (ArgumentException exception)
        {
            throw new NativeExternalLaunchException(
                "The selected path is invalid.",
                exception);
        }

        if (!IsWithin(normalizedRoot, requestedPath))
        {
            throw new NativeExternalLaunchException(
                "The selected path is outside the repository and was not opened.");
        }

        var targetPath = requestedPath;
        var targetExists = File.Exists(targetPath) || Directory.Exists(targetPath);
        var usedExistingParentForMissingTarget = false;
        if (!targetExists)
        {
            if (!allowMissingTarget)
            {
                throw new NativeExternalLaunchException(
                    "The selected file is no longer available.");
            }

            targetPath = FindExistingParent(normalizedRoot, requestedPath);
            usedExistingParentForMissingTarget = true;
        }

        EnsureNoReparsePoint(
            targetPath,
            "The selected path cannot be opened because a reparse point prevents safe containment verification.");
        EnsureNoReparseAncestors(normalizedRoot, targetPath);
        return new ResolvedRepositoryPath(
            normalizedRoot,
            targetPath,
            usedExistingParentForMissingTarget);
    }

    private static string FindExistingParent(string root, string requestedPath)
    {
        var candidate = requestedPath;
        while (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            var parent = Directory.GetParent(candidate)?.FullName;
            if (parent is null || !IsWithin(root, parent))
            {
                return root;
            }

            candidate = parent;
        }

        return Directory.Exists(candidate) ? candidate : root;
    }

    private static void EnsureNoReparseAncestors(string root, string target)
    {
        var current = target;
        while (true)
        {
            EnsureNoReparsePoint(
                current,
                "The selected path cannot be opened because a reparse point prevents safe containment verification.");
            if (PathsEqual(current, root))
            {
                return;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || !IsWithin(root, parent))
            {
                throw new NativeExternalLaunchException(
                    "The selected path is outside the repository and was not opened.");
            }

            current = parent;
        }
    }

    private static void EnsureNoReparsePoint(string path, string message)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new NativeExternalLaunchException(message);
            }
        }
        catch (NativeExternalLaunchException)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new NativeExternalLaunchException(message, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new NativeExternalLaunchException(message, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new NativeExternalLaunchException(message, exception);
        }
        catch (IOException exception)
        {
            throw new NativeExternalLaunchException(message, exception);
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = TrimTrailingSeparators(root);
        var normalizedCandidate = TrimTrailingSeparators(candidate);
        var boundary = normalizedRoot.EndsWith(
                Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal)
            || normalizedRoot.EndsWith(
                Path.AltDirectorySeparatorChar.ToString(),
                StringComparison.Ordinal)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return PathsEqual(normalizedRoot, normalizedCandidate)
            || normalizedCandidate.StartsWith(
                boundary,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            TrimTrailingSeparators(left),
            TrimTrailingSeparators(right),
            StringComparison.OrdinalIgnoreCase);

    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed)
            ? root ?? trimmed
            : trimmed;
    }

    private static bool TryGetExecutablePath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            var extension = Path.GetExtension(fullPath);
            if ((!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase))
                || !File.Exists(fullPath))
            {
                return false;
            }

            normalizedPath = fullPath;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record ResolvedRepositoryPath(
        string RepositoryRoot,
        string TargetPath,
        bool UsedExistingParentForMissingTarget);
}
