using System.Runtime.InteropServices;

namespace WinGit.Core.Codex;

/// <summary>Finds an installed native Codex executable without invoking a shell.</summary>
public static class CodexExecutableLocator
{
    public static string Resolve(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var candidate = explicitPath.Trim();
            if (LooksLikePath(candidate) && !File.Exists(candidate))
            {
                throw new FileNotFoundException(
                    "The configured Codex executable was not found.", candidate);
            }

            return candidate;
        }

        return FindOnPath() ?? throw new FileNotFoundException(
            "Codex CLI was not found. Install Codex and make it available on PATH before using WinGit's Codex features.");
    }

    /// <summary>Finds a native executable on PATH, including the binary behind an npm CLI shim.</summary>
    public static string? FindOnPath(string? searchPath = null)
    {
        var path = searchPath ?? Environment.GetEnvironmentVariable("PATH");
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

            try
            {
                var executableName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
                var directPath = Path.Combine(directory, executableName);
                if (File.Exists(directPath))
                {
                    return Path.GetFullPath(directPath);
                }

                if (!OperatingSystem.IsWindows() ||
                    (!File.Exists(Path.Combine(directory, "codex.cmd")) &&
                     !File.Exists(Path.Combine(directory, "codex.ps1"))))
                {
                    continue;
                }

                var architecture = RuntimeInformation.ProcessArchitecture;
                var targetTriple = architecture switch
                {
                    Architecture.X64 => "x86_64-pc-windows-msvc",
                    Architecture.Arm64 => "aarch64-pc-windows-msvc",
                    _ => null
                };
                if (targetTriple is null)
                {
                    continue;
                }

                var packageName = architecture == Architecture.Arm64
                    ? "codex-win32-arm64"
                    : "codex-win32-x64";
                foreach (var packageRoot in new[]
                {
                    Path.Combine(directory, "node_modules", "@openai", "codex", "node_modules", "@openai", packageName),
                    Path.Combine(directory, "node_modules", "@openai", packageName)
                })
                {
                    var packageExecutable = Path.Combine(
                        packageRoot, "vendor", targetTriple, "bin", "codex.exe");
                    if (File.Exists(packageExecutable))
                    {
                        return Path.GetFullPath(packageExecutable);
                    }
                }
            }
            catch (ArgumentException)
            {
                // An invalid PATH entry should not hide later installations.
            }
        }

        return null;
    }

    private static bool LooksLikePath(string value) =>
        Path.IsPathFullyQualified(value) ||
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar) ||
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}
