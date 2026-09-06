using WinGit.Core;

namespace WinGit.Native;

/// <summary>
/// Resolves the Git tree that is shipped beside the native executable. It
/// never searches the Electron checkout or falls back to the machine PATH.
/// </summary>
public static class NativeGitRuntime
{
    public const string RuntimeDirectoryName = "git";

    public static string ResolveGitRoot(string? applicationRoot = null)
    {
        var root = applicationRoot ?? AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "The native application root is unavailable.");
        }

        var gitRoot = Path.GetFullPath(Path.Combine(root, RuntimeDirectoryName));
        if (!Directory.Exists(gitRoot))
        {
            throw new InvalidOperationException(
                "The bundled Git runtime is missing from the native application output.");
        }

        return gitRoot;
    }

    public static string ResolveGitExecutable(string? applicationRoot = null)
    {
        var executable = Path.Combine(
            ResolveGitRoot(applicationRoot),
            "cmd",
            "git.exe");
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                "The bundled Git executable is missing from the native application output.");
        }

        return executable;
    }

    /// <summary>Creates the repository service with the contained Git executable.</summary>
    public static GitRepositoryService CreateRepositoryService(
        string? applicationRoot = null) =>
        new(ResolveGitExecutable(applicationRoot));
}
