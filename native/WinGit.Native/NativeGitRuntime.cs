using System.ComponentModel;
using System.Diagnostics;
using WinGit.Core;

namespace WinGit.Native;

/// <summary>
/// Resolves the Git tree that is shipped beside the native executable. It
/// never searches the Electron checkout or falls back to the machine PATH.
/// </summary>
public static class NativeGitRuntime
{
    public const string RuntimeDirectoryName = "git";
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VersionOutputTimeout = TimeSpan.FromSeconds(1);

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

    public static async Task<NativeGitRuntimeValidation> ValidateAsync(
        string? applicationRoot = null)
    {
        var expectedExecutable = GetExpectedGitExecutablePath(applicationRoot);
        string executable;
        try
        {
            executable = ResolveGitExecutable(applicationRoot);
        }
        catch (InvalidOperationException exception)
        {
            return NativeGitRuntimeValidation.Invalid(expectedExecutable, exception.Message);
        }

        return await ProbeVersionAsync(executable).ConfigureAwait(true);
    }

    /// <summary>Creates the repository service with the contained Git executable.</summary>
    public static GitRepositoryService CreateRepositoryService(
        string? applicationRoot = null) =>
        new(ResolveGitExecutable(applicationRoot));

    private static string GetExpectedGitExecutablePath(string? applicationRoot)
    {
        var root = applicationRoot ?? AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            return Path.Combine(RuntimeDirectoryName, "cmd", "git.exe");
        }

        return Path.GetFullPath(Path.Combine(root, RuntimeDirectoryName, "cmd", "git.exe"));
    }

    private static async Task<NativeGitRuntimeValidation> ProbeVersionAsync(string executable)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--version");

        try
        {
            if (!process.Start())
            {
                return NativeGitRuntimeValidation.Invalid(
                    executable,
                    "The bundled Git executable could not be started.");
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(VersionProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                Terminate(process);
                return NativeGitRuntimeValidation.Invalid(
                    executable,
                    "The bundled Git executable did not finish its version check in time.");
            }

            try
            {
                await Task.WhenAll(standardOutputTask, standardErrorTask)
                    .WaitAsync(VersionOutputTimeout)
                    .ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                Terminate(process);
                return NativeGitRuntimeValidation.Invalid(
                    executable,
                    "The bundled Git executable did not return a complete version check.");
            }

            var standardOutput = await standardOutputTask.ConfigureAwait(true);
            _ = await standardErrorTask.ConfigureAwait(true);
            if (process.ExitCode != 0)
            {
                return NativeGitRuntimeValidation.Invalid(
                    executable,
                    "The bundled Git executable returned an error for its version check.");
            }

            if (!IsValidVersionOutput(standardOutput))
            {
                return NativeGitRuntimeValidation.Invalid(
                    executable,
                    "The bundled Git executable returned an invalid version check.");
            }

            return NativeGitRuntimeValidation.Valid(executable);
        }
        catch (Exception exception) when (
            exception is Win32Exception
            or UnauthorizedAccessException
            or InvalidOperationException
            or IOException)
        {
            return NativeGitRuntimeValidation.Invalid(
                executable,
                $"The bundled Git executable could not be started: {exception.Message}");
        }
        finally
        {
            Terminate(process);
        }
    }

    private static bool IsValidVersionOutput(string output)
    {
        var line = output.Trim();
        const string prefix = "git version ";
        return line.StartsWith(prefix, StringComparison.Ordinal)
            && line[prefix.Length..].Length > 0
            && !line.Contains('\r')
            && !line.Contains('\n');
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}

public sealed record NativeGitRuntimeValidation(
    string GitExecutablePath,
    string? FailureReason)
{
    public bool IsValid => FailureReason is null;

    public static NativeGitRuntimeValidation Valid(string executablePath) =>
        new(executablePath, null);

    public static NativeGitRuntimeValidation Invalid(string executablePath, string reason) =>
        new(executablePath, reason);
}
