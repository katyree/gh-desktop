using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public enum GitSshMode
{
    Bundled,
    SystemOpenSsh,
}

public sealed record GitProcessOptions
{
    public GitSshMode SshMode { get; init; } = GitSshMode.Bundled;

    public string? SshExecutablePath { get; init; }

    /// <summary>
    /// Selects Git Credential Manager for generic HTTPS remote operations.
    /// GitHub hosts remain on the native account boundary.
    /// </summary>
    public bool UseExternalCredentialHelper { get; init; }

    public static GitProcessOptions Default { get; } = new();
}

internal sealed class GitProcessRunner
{
    internal const int MaxOutputBytes = 16 * 1024 * 1024;

    private static readonly string[] EnvironmentKeysToRemove =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_COMMON_DIR",
        "GIT_CEILING_DIRECTORIES",
        "GIT_TRACE",
        "GIT_TRACE2",
        "GIT_TRACE2_EVENT",
        "GIT_TRACE2_PERF",
        "GIT_CURL_VERBOSE",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT",
        "GIT_SSH",
        "GIT_SSH_COMMAND",
        "GIT_ASKPASS",
        "SSH_ASKPASS",
        "DISPLAY",
        "GCM_INTERACTIVE",
        "GCM_GUI_SOFTWARE_RENDERING",
    ];

    private static readonly string[] ProtectedEnvironmentKeys =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_COMMON_DIR",
        "GIT_CEILING_DIRECTORIES",
        "GIT_TRACE",
        "GIT_TRACE2",
        "GIT_TRACE2_EVENT",
        "GIT_TRACE2_PERF",
        "GIT_CURL_VERBOSE",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT",
        "GIT_SSH",
        "GIT_SSH_COMMAND",
        "GIT_TERMINAL_PROMPT",
    ];

    private readonly string gitExecutable;
    private GitProcessOptions processOptions;

    internal GitProcessRunner(
        string gitExecutable,
        GitProcessOptions? processOptions = null)
    {
        this.gitExecutable = gitExecutable;
        this.processOptions = processOptions ?? GitProcessOptions.Default;
        ValidateProcessOptions(this.processOptions);
    }

    internal void UpdateProcessOptions(GitProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateProcessOptions(options);
        Interlocked.Exchange(ref processOptions, options);
    }

    internal async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? expectedExitCodes = null,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null,
        bool isBackgroundTask = false,
        GitProcessOptions? processOptionsOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                workingDirectory,
                arguments,
                standardInput is not null,
                environmentOverrides,
                isBackgroundTask,
                processOptionsOverride ?? Volatile.Read(ref processOptions)),
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start Git.");
        }

        var standardOutputTask = ReadBoundedAsync(process.StandardOutput.BaseStream);
        var standardErrorTask = ReadBoundedAsync(process.StandardError.BaseStream);
        using var cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!),
            process);

        try
        {
            if (standardInput is not null)
            {
                await WriteStandardInputAsync(process.StandardInput, standardInput).ConfigureAwait(false);
            }

            await Task.WhenAll(
                process.WaitForExitAsync(),
                standardOutputTask,
                standardErrorTask).ConfigureAwait(false);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await DrainAfterCancellationAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var result = new GitProcessResult(
            process.ExitCode,
            standardOutputTask.Result.Bytes,
            standardErrorTask.Result.Bytes,
            standardOutputTask.Result.Truncated,
            standardErrorTask.Result.Truncated);

        var isExpectedExitCode = result.ExitCode == 0 || expectedExitCodes?.Contains(result.ExitCode) == true;
        if (!isExpectedExitCode)
        {
            var error = DecodeAndSanitize(result.StandardError);
            var suffix = string.IsNullOrEmpty(error) ? string.Empty : $": {error}";
            throw new GitCommandException(
                $"Git command failed with exit code {result.ExitCode}{suffix}",
                result.ExitCode,
                error);
        }

        return result;
    }

    private ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool redirectStandardInput,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        bool isBackgroundTask,
        GitProcessOptions processOptions)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = gitExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = redirectStandardInput
                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                : null,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (ShouldRemoveEnvironmentKey(key))
            {
                startInfo.Environment.Remove(key);
            }
        }

        ConfigureContainedGitEnvironment(startInfo);
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        ApplyEnvironmentOverrides(startInfo, environmentOverrides, nameof(environmentOverrides));
        ConfigureSshEnvironment(startInfo, processOptions, isBackgroundTask);
        ConfigureCredentialManagerEnvironment(startInfo, arguments, isBackgroundTask);

        // Never interactively prompt for credentials, even as a fallback.
        // Credential flows use the helper protocol or an explicit UI prompt.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // Keep this option in the process boundary so every read-only command avoids
        // an external fsmonitor hook, even when the repository config enables one.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.fsmonitor=false");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void ConfigureSshEnvironment(
        ProcessStartInfo startInfo,
        GitProcessOptions options,
        bool isBackgroundTask)
    {
        if (options.SshMode != GitSshMode.SystemOpenSsh)
        {
            if (isBackgroundTask)
            {
                startInfo.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
            }

            return;
        }

        var executable = options.SshExecutablePath;
        if (string.IsNullOrWhiteSpace(executable)
            || !Path.IsPathRooted(executable)
            || executable.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
        {
            throw new ArgumentException(
                "System OpenSSH requires an absolute executable path without control characters or quotes.",
                nameof(options));
        }

        var command = executable.Contains(' ')
            ? $"\"{executable}\""
            : executable;
        startInfo.Environment["GIT_SSH_COMMAND"] = isBackgroundTask
            ? $"{command} -o BatchMode=yes"
            : command;
    }

    private static void ConfigureCredentialManagerEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyList<string> arguments,
        bool isBackgroundTask)
    {
        if (!IsGitCredentialManagerCommand(arguments))
        {
            return;
        }

        // Match Electron's explicit credential.ts environment. This branch is
        // reached only when the caller already selected manager for one
        // command; commands without the explicit manager config never inherit GCM.
        startInfo.Environment["GIT_ASKPASS"] = string.Empty;
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["GCM_INTERACTIVE"] = isBackgroundTask ? "0" : "1";
    }

    private static bool IsGitCredentialManagerCommand(IReadOnlyList<string> arguments)
    {
        return arguments.Any(argument =>
            string.Equals(argument, "credential.helper=manager", StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyEnvironmentOverrides(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? overrides,
        string parameterName)
    {
        if (overrides is null)
        {
            return;
        }

        foreach (var (key, value) in overrides)
        {
            ValidateEnvironmentEntry(key, value, parameterName);
            if (value is null)
            {
                startInfo.Environment.Remove(key);
            }
            else
            {
                startInfo.Environment[key] = value;
            }
        }
    }

    private static void ValidateEnvironmentEntry(
        string key,
        string? value,
        string parameterName)
    {
        if (string.IsNullOrEmpty(key)
            || key.IndexOfAny(['\0', '\r', '\n']) >= 0
            || ProtectedEnvironmentKeys.Any(protectedKey =>
                string.Equals(protectedKey, key, StringComparison.OrdinalIgnoreCase))
            || key.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The environment override '{key}' is not permitted.",
                parameterName);
        }

        if (value?.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                $"The environment override '{key}' contains a control character.",
                parameterName);
        }
    }

    private static void ValidateProcessOptions(GitProcessOptions options)
    {
        if (!Enum.IsDefined(options.SshMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SshMode,
                "The Git SSH mode is not supported.");
        }

        if (options.SshMode == GitSshMode.SystemOpenSsh
            && (string.IsNullOrWhiteSpace(options.SshExecutablePath)
                || !Path.IsPathRooted(options.SshExecutablePath)
                || options.SshExecutablePath.IndexOfAny(['\0', '\r', '\n', '"']) >= 0
                || !File.Exists(options.SshExecutablePath)))
        {
            throw new ArgumentException(
                "System OpenSSH requires an existing absolute executable path without control characters or quotes.",
                nameof(options));
        }
    }

    private static bool ShouldRemoveEnvironmentKey(string key) =>
        EnvironmentKeysToRemove.Any(environmentKey =>
            string.Equals(environmentKey, key, StringComparison.OrdinalIgnoreCase))
        || key.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase);

    private void ConfigureContainedGitEnvironment(ProcessStartInfo startInfo)
    {
        if (!Path.IsPathRooted(gitExecutable))
        {
            return;
        }

        var commandDirectory = Path.GetDirectoryName(Path.GetFullPath(gitExecutable));
        if (commandDirectory is null ||
            !string.Equals(
                Path.GetFileName(commandDirectory),
                "cmd",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var gitRoot = Directory.GetParent(commandDirectory)?.FullName;
        if (gitRoot is null)
        {
            return;
        }

        var mingwBin = Path.Combine(gitRoot, "mingw64", "bin");
        var execPath = Path.Combine(gitRoot, "mingw64", "libexec", "git-core");
        if (!File.Exists(Path.Combine(mingwBin, "git.exe")) ||
            !Directory.Exists(execPath))
        {
            return;
        }

        var pathEntries = new List<string> { mingwBin };
        var usrBin = Path.Combine(gitRoot, "usr", "bin");
        if (Directory.Exists(usrBin))
        {
            pathEntries.Add(usrBin);
        }

        // Keep compatibility with distributions that place MSYS tools below
        // mingw64, while accepting the current Dugite tree's root-level usr.
        var nestedUsrBin = Path.Combine(gitRoot, "mingw64", "usr", "bin");
        if (Directory.Exists(nestedUsrBin))
        {
            pathEntries.Add(nestedUsrBin);
        }

        if (startInfo.Environment.TryGetValue("PATH", out var existingPath) &&
            !string.IsNullOrEmpty(existingPath))
        {
            pathEntries.Add(existingPath);
        }

        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, pathEntries);
        startInfo.Environment["GIT_EXEC_PATH"] = execPath;
    }

    private static async Task WriteStandardInputAsync(StreamWriter writer, string input)
    {
        try
        {
            await writer.WriteAsync(input).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            writer.Close();
        }
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(Stream stream)
    {
        var bytes = new MemoryStream(capacity: 64 * 1024);
        var buffer = new byte[64 * 1024];
        var truncated = false;

        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var remaining = MaxOutputBytes - bytes.Length;
            if (remaining > 0)
            {
                var toCopy = (int)Math.Min(remaining, count);
                bytes.Write(buffer, 0, toCopy);
                truncated |= toCopy != count;
            }
            else
            {
                truncated = true;
            }
        }

        return new BoundedOutput(bytes.ToArray(), truncated);
    }

    private static async Task DrainAfterCancellationAsync(
        Task<BoundedOutput> standardOutputTask,
        Task<BoundedOutput> standardErrorTask)
    {
        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation is the caller's result; stream teardown errors are secondary.
        }
    }

    private static void TryKill(Process process)
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
            // The process exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process is already gone or cannot be opened for termination.
        }
    }

    private static string DecodeAndSanitize(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        // Git can echo a remote URL in an error. Remove all URL userinfo, including
        // token-only forms such as https://TOKEN@host, before this reaches callers.
        text = Regex.Replace(text, @"(?i)(https?://)[^\s/@]+@", "$1<redacted>@");
        text = Regex.Replace(
            text,
            @"(?i)([?&](?:access_token|token|auth|password|passwd|secret|client_secret)=)[^&\s]+",
            "$1<redacted>");

        return text.Length <= 4096 ? text : text[..4096] + "…";
    }

    private sealed record BoundedOutput(byte[] Bytes, bool Truncated);
}

internal sealed record GitProcessResult(
    int ExitCode,
    byte[] StandardOutput,
    byte[] StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated);
