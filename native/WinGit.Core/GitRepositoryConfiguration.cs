namespace WinGit.Core;

public sealed partial class GitRepositoryService
{
    /// <summary>Reads the allowlisted values from one selected Git configuration scope.</summary>
    public async Task<GitConfigValues> GetGitConfigValuesAsync(
        string? root,
        GitConfigScope scope,
        CancellationToken cancellationToken)
    {
        var workingDirectory = GetConfigurationWorkingDirectory(root, scope);
        var userName = await ReadScopedConfigValueAsync(
            workingDirectory,
            scope,
            GitConfigSetting.UserName,
            cancellationToken).ConfigureAwait(false);
        var userEmail = await ReadScopedConfigValueAsync(
            workingDirectory,
            scope,
            GitConfigSetting.UserEmail,
            cancellationToken).ConfigureAwait(false);
        var defaultBranch = await ReadScopedConfigValueAsync(
            workingDirectory,
            scope,
            GitConfigSetting.DefaultBranch,
            cancellationToken).ConfigureAwait(false);

        return new GitConfigValues(userName, userEmail, defaultBranch);
    }

    /// <summary>Writes one allowlisted value to the selected Git configuration scope.</summary>
    public async Task SetGitConfigValueAsync(
        string? root,
        GitConfigScope scope,
        GitConfigSetting setting,
        string value,
        CancellationToken cancellationToken)
    {
        var workingDirectory = GetConfigurationWorkingDirectory(root, scope);
        var key = GetGitConfigKey(setting);
        if (setting == GitConfigSetting.DefaultBranch)
        {
            await ValidateBranchNameAsync(workingDirectory, value, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ValidateTextConfigValue(value, setting);
        }

        await ExecuteMutationAsync(
            workingDirectory,
            cancellationToken,
            async path =>
            {
                var result = await processRunner.RunAsync(
                    path,
                    ["config", GetScopeArgument(scope), "--replace-all", key, value],
                    cancellationToken,
                    environmentOverrides: gitEnvironmentOverrides).ConfigureAwait(false);
                EnsureComplete(result, $"{key} update");
            }).ConfigureAwait(false);
    }

    private async Task<string?> ReadScopedConfigValueAsync(
        string workingDirectory,
        GitConfigScope scope,
        GitConfigSetting setting,
        CancellationToken cancellationToken)
    {
        var key = GetGitConfigKey(setting);
        var result = await processRunner.RunAsync(
            workingDirectory,
            ["config", GetScopeArgument(scope), "--null", "--get", key],
            cancellationToken,
            expectedExitCodes: [1],
            environmentOverrides: gitEnvironmentOverrides).ConfigureAwait(false);
        EnsureComplete(result, $"{key} lookup");
        if (result.ExitCode == 1)
        {
            return null;
        }

        var value = DecodeUtf8(result.StandardOutput, $"{key} lookup");
        var terminator = value.IndexOf('\0');
        return terminator >= 0 ? value[..terminator] : value;
    }

    private static string GetConfigurationWorkingDirectory(string? root, GitConfigScope scope) =>
        scope switch
        {
            GitConfigScope.Local => ValidateDirectory(root!, nameof(root)),
            GitConfigScope.Global => ValidateDirectory(
                AppContext.BaseDirectory,
                "global configuration working directory"),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Git configuration scope."),
        };

    private static string GetScopeArgument(GitConfigScope scope) =>
        scope switch
        {
            GitConfigScope.Local => "--local",
            GitConfigScope.Global => "--global",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown Git configuration scope."),
        };

    private static string GetGitConfigKey(GitConfigSetting setting) =>
        setting switch
        {
            GitConfigSetting.UserName => "user.name",
            GitConfigSetting.UserEmail => "user.email",
            GitConfigSetting.DefaultBranch => "init.defaultBranch",
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown Git configuration setting."),
        };

    private static void ValidateTextConfigValue(string value, GitConfigSetting setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));
        if (value.IndexOf('\0') >= 0 || value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException(
                $"The {setting} Git configuration value cannot contain NUL or line breaks.",
                nameof(value));
        }
    }
}
