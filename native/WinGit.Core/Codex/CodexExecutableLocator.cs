using System.Runtime.InteropServices;

namespace WinGit.Core.Codex;

/// <summary>
/// Finds the native Codex executable without invoking a shell or changing the
/// user's Codex profile. The packaged layout matches WinGit's Electron runtime.
/// </summary>
public static class CodexExecutableLocator
{
    public static string Resolve(
        string? explicitPath = null,
        string? applicationRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var candidate = explicitPath.Trim();
            if (LooksLikePath(candidate) && !File.Exists(candidate))
            {
                throw new FileNotFoundException(
                    "The configured Codex executable was not found.",
                    candidate);
            }

            return candidate;
        }

        foreach (var root in CandidateRoots(applicationRoot))
        {
            if (OperatingSystem.IsWindows())
            {
                var bundledPath = GetBundledPath(root);
                if (File.Exists(bundledPath))
                {
                    return bundledPath;
                }

                var sourcePackagePath = GetSourcePackagePath(root);
                if (File.Exists(sourcePackagePath))
                {
                    return sourcePackagePath;
                }
            }
        }

        var pathExecutable = FindOnPath();
        // Keep the command name as the final fallback so ProcessStartInfo can
        // report the platform's normal executable-resolution error. This also
        // supports installations whose launcher is provisioned after startup.
        return pathExecutable ?? (OperatingSystem.IsWindows() ? "codex.exe" : "codex");
    }

    /// <summary>Returns the native executable in the Electron package layout.</summary>
    public static string GetBundledPath(
        string applicationRoot,
        Architecture? architecture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        var targetTriple = GetWindowsTargetTriple(architecture ?? RuntimeInformation.ProcessArchitecture);
        return Path.Combine(
            applicationRoot,
            "codex",
            "vendor",
            targetTriple,
            "bin",
            "codex.exe");
    }

    /// <summary>Returns a packaged source-tree runtime path when present.</summary>
    public static string GetSourcePackagePath(
        string projectRoot,
        Architecture? architecture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var selectedArchitecture = architecture ?? RuntimeInformation.ProcessArchitecture;
        var targetTriple = GetWindowsTargetTriple(selectedArchitecture);
        var packageName = selectedArchitecture == Architecture.Arm64
            ? "codex-win32-arm64"
            : "codex-win32-x64";
        return Path.Combine(
            projectRoot,
            "app",
            "node_modules",
            "@openai",
            packageName,
            "vendor",
            targetTriple,
            "bin",
            "codex.exe");
    }

    /// <summary>Finds a native <c>codex.exe</c> on PATH without shell execution.</summary>
    public static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var rawDirectory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            var executableName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
            string candidate;
            try
            {
                candidate = Path.Combine(directory, executableName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots(string? applicationRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(applicationRoot))
        {
            roots.Add(Path.GetFullPath(applicationRoot));
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 5; depth++)
        {
            roots.Add(current.FullName);
            current = current.Parent;
        }

        return roots;
    }

    private static string GetWindowsTargetTriple(Architecture architecture)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The packaged Codex runtime is available only on Windows.");
        }

        return architecture switch
        {
            Architecture.X64 => "x86_64-pc-windows-msvc",
            Architecture.Arm64 => "aarch64-pc-windows-msvc",
            _ => throw new PlatformNotSupportedException(
                $"The packaged Codex runtime is not available for {architecture}.")
        };
    }

    private static bool LooksLikePath(string value) =>
        Path.IsPathFullyQualified(value) ||
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar) ||
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}
