using System.ComponentModel;
using System.Diagnostics;
using WinGit.Core;

namespace WinGit.Native;

/// <summary>
/// Resolves and checks the user's installed Git for Windows executable.
/// </summary>
public static class NativeGitRuntime
{
    public const string SystemOpenSshRelativePath = "System32\\OpenSSH\\ssh.exe";
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VersionOutputTimeout = TimeSpan.FromSeconds(1);

    public static async Task<NativeGitRuntimeValidation> ValidateAsync()
    {
        string executable;
        try
        {
            executable = GitExecutableLocator.Resolve();
        }
        catch (FileNotFoundException exception)
        {
            return NativeGitRuntimeValidation.Invalid("git.exe", exception.Message);
        }

        return await ProbeVersionAsync(executable).ConfigureAwait(true);
    }

    public static bool IsSystemOpenSshAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return File.Exists(GetSystemOpenSshPath());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static string ResolveSystemOpenSshExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "System OpenSSH is available only on Windows.");
        }

        var executable = GetSystemOpenSshPath();
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                $"The Windows OpenSSH executable is missing at '{executable}'.");
        }

        return executable;
    }

    public static GitProcessOptions CreateGitProcessOptions(
        bool useSystemOpenSsh,
        bool useExternalCredentialHelper = false)
    {
        if (!useSystemOpenSsh
            && !useExternalCredentialHelper)
        {
            return GitProcessOptions.Default;
        }

        return new GitProcessOptions
        {
            SshMode = useSystemOpenSsh ? GitSshMode.SystemOpenSsh : GitSshMode.Bundled,
            SshExecutablePath = useSystemOpenSsh
                ? ResolveSystemOpenSshExecutable()
                : null,
            UseExternalCredentialHelper = useExternalCredentialHelper,
        };
    }

    private static string GetSystemOpenSshPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            throw new InvalidOperationException("The Windows directory is unavailable.");
        }

        return Path.GetFullPath(Path.Combine(windowsDirectory, SystemOpenSshRelativePath));
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
                    "The installed Git executable could not be started.");
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
                    "The installed Git executable did not finish its version check in time.");
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
                    "The installed Git executable did not return a complete version check.");
            }

            var standardOutput = await standardOutputTask.ConfigureAwait(true);
            _ = await standardErrorTask.ConfigureAwait(true);
            if (process.ExitCode != 0)
            {
                return NativeGitRuntimeValidation.Invalid(
                    executable,
                    "The installed Git executable returned an error for its version check.");
            }

            if (!IsValidVersionOutput(standardOutput))
            {
                return NativeGitRuntimeValidation.Invalid(
                    executable,
                    "The installed Git executable returned an invalid version check.");
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
                $"The installed Git executable could not be started: {exception.Message}");
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
