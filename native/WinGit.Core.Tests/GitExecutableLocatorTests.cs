using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitExecutableLocatorTests
{
    [Fact]
    public void FindsInstalledGitOnPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wingit-git-locator-{Guid.NewGuid():N}");
        var gitDirectory = Path.Combine(directory, "cmd");
        var executable = Path.Combine(gitDirectory, "git.exe");

        try
        {
            Directory.CreateDirectory(gitDirectory);
            File.WriteAllText(executable, "test executable");

            Assert.Equal(executable, GitExecutableLocator.FindOnPath(gitDirectory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
