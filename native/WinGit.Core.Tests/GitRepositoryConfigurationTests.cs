using System.Diagnostics;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryConfigurationTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private readonly string fixtureRoot;
    private readonly string repositoryRoot;
    private readonly string globalConfigPath;

    public GitRepositoryConfigurationTests()
    {
        Directory.CreateDirectory(FixtureParent);
        fixtureRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        repositoryRoot = Path.Combine(fixtureRoot, "repository");
        globalConfigPath = Path.Combine(fixtureRoot, "global.gitconfig");
        Directory.CreateDirectory(repositoryRoot);

        RunGit(repositoryRoot, "init");
        RunGit(repositoryRoot, "config", "--local", "color.ui", "always");
        RunGit(repositoryRoot, "config", "--file", globalConfigPath, "color.ui", "always");
    }

    [Fact]
    public async Task ScopedValuesReadAndWriteWithoutChangingOtherScopes()
    {
        var service = new GitRepositoryService(
            gitExecutable: null,
            environmentOverrides: new Dictionary<string, string?>
            {
                ["GIT_CONFIG_GLOBAL"] = globalConfigPath,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            });

        await service.SetGitConfigValueAsync(
            repositoryRoot,
            GitConfigScope.Local,
            GitConfigSetting.UserName,
            "Fixture Local User",
            CancellationToken.None);
        await service.SetGitConfigValueAsync(
            repositoryRoot,
            GitConfigScope.Local,
            GitConfigSetting.UserEmail,
            "fixture-local@example.invalid",
            CancellationToken.None);
        await service.SetGitConfigValueAsync(
            repositoryRoot,
            GitConfigScope.Local,
            GitConfigSetting.DefaultBranch,
            "local-main",
            CancellationToken.None);

        await service.SetGitConfigValueAsync(
            root: null,
            GitConfigScope.Global,
            GitConfigSetting.UserName,
            "Fixture Global User",
            CancellationToken.None);
        await service.SetGitConfigValueAsync(
            root: null,
            GitConfigScope.Global,
            GitConfigSetting.UserEmail,
            "fixture-global@example.invalid",
            CancellationToken.None);
        await service.SetGitConfigValueAsync(
            root: null,
            GitConfigScope.Global,
            GitConfigSetting.DefaultBranch,
            "global-main",
            CancellationToken.None);

        var local = await service.GetGitConfigValuesAsync(
            repositoryRoot,
            GitConfigScope.Local,
            CancellationToken.None);
        var global = await service.GetGitConfigValuesAsync(
            root: null,
            GitConfigScope.Global,
            CancellationToken.None);

        Assert.Equal("Fixture Local User", local.UserName);
        Assert.Equal("fixture-local@example.invalid", local.UserEmail);
        Assert.Equal("local-main", local.DefaultBranch);
        Assert.Equal("Fixture Global User", global.UserName);
        Assert.Equal("fixture-global@example.invalid", global.UserEmail);
        Assert.Equal("global-main", global.DefaultBranch);

        Assert.Equal("always", RunGit(repositoryRoot, "config", "--local", "--get", "color.ui").Trim());
        Assert.Equal(
            "always",
            RunGit(repositoryRoot, "config", "--file", globalConfigPath, "--get", "color.ui").Trim());
    }

    public void Dispose() => DeleteDirectory(fixtureRoot);

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Git fixture.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git fixture command failed: {error}");
        }

        return output;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fixtureRoot = FixtureParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = Path.GetRelativePath(fixtureRoot, fullPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath is "." or ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Fixture cleanup target is outside the test fixture directory.");
        }

        foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var directory in Directory.EnumerateDirectories(fullPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        Directory.Delete(fullPath, recursive: true);
    }
}
