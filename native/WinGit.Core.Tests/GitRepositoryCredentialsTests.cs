using System.Diagnostics;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitRepositoryCredentialsTests : IDisposable
{
    private static readonly string FixtureParent = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "WinGit.Core.Tests"));
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string repositoryRoot;

    public GitRepositoryCredentialsTests()
    {
        Directory.CreateDirectory(FixtureParent);
        repositoryRoot = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        RunGit(repositoryRoot, "init", "-b", "main");
        RunGit(repositoryRoot, "config", "user.name", "Test User");
        RunGit(repositoryRoot, "config", "user.email", "test-user@example.invalid");
        RunGit(repositoryRoot, "config", "commit.gpgsign", "false");
        var hooksPath = Path.Combine(repositoryRoot, ".hooks");
        Directory.CreateDirectory(hooksPath);
        RunGit(repositoryRoot, "config", "core.hooksPath", hooksPath);
    }

    [Fact]
    public void CredentialProtocolParsesExpandsAndFormatsEntries()
    {
        var parsed = GitRepositoryService.ParseCredential(
            "protocol=https\nhost=example.test\nusername=alice\npassword=s3cret\n\nurl[]=first\nurl[]=second\nnot-a-pair\n");

        Assert.Equal("https", parsed["protocol"]);
        Assert.Equal("alice", parsed["username"]);
        Assert.Equal("s3cret", parsed["password"]);
        Assert.Equal("first", parsed["url[0]"]);
        Assert.Equal("second", parsed["url[1]"]);

        var formatted = GitRepositoryService.FormatCredential(new Dictionary<string, string>
        {
            ["protocol"] = "https",
            ["url[0]"] = "first",
            ["url[1]"] = "second",
        });
        Assert.Contains("url[]=first\n", formatted, StringComparison.Ordinal);
        Assert.Contains("url[]=second\n", formatted, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => GitRepositoryService.FormatCredential(
            new Dictionary<string, string> { ["password"] = "a\nb" }));
        Assert.Throws<ArgumentException>(() => GitRepositoryService.FormatCredential(
            new Dictionary<string, string> { ["password"] = "a\0b" }));
    }

    [Fact]
    public async Task CredentialStoreRoundTripApprovesFillsAndRejects()
    {
        var storePath = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N") + ".txt");
        var helper = $"store --file={storePath}";
        var service = new GitRepositoryService();
        var credential = new Dictionary<string, string>
        {
            ["url"] = "https://example.test",
            ["username"] = "alice",
            ["password"] = "s3cret",
        };

        try
        {
            await service.ApproveCredentialAsync(repositoryRoot, credential, helper, CancellationToken.None);
            var filled = await service.FillCredentialAsync(
                repositoryRoot,
                new Dictionary<string, string> { ["url"] = "https://example.test" },
                helper,
                CancellationToken.None);

            Assert.Equal("alice", filled["username"]);
            Assert.Equal("s3cret", filled["password"]);

            await service.RejectCredentialAsync(repositoryRoot, credential, helper, CancellationToken.None);
            var miss = await Assert.ThrowsAsync<GitCommandException>(
                () => service.FillCredentialAsync(
                    repositoryRoot,
                    new Dictionary<string, string> { ["url"] = "https://example.test" },
                    helper,
                    CancellationToken.None));
            // The miss proves the non-interactive default: Git reports
            // disabled terminal prompts instead of hanging or popping UI.
            Assert.Contains("terminal prompts disabled", miss.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(storePath);
        }
    }

    [Fact]
    public void AuthenticationFailureIdentificationNamesTheHost()
    {
        var failed = GitRepositoryService.IdentifyAuthenticationFailure(
            "fatal: Authentication failed for 'https://github.com/o/r.git'",
            "github.com");
        Assert.NotNull(failed);
        Assert.Contains("github.com", failed!, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", failed!, StringComparison.Ordinal);

        var prompt = GitRepositoryService.IdentifyAuthenticationFailure(
            "fatal: could not read Username for 'https://example.test': terminal prompts disabled",
            "example.test");
        Assert.NotNull(prompt);
        Assert.Contains("example.test", prompt!, StringComparison.Ordinal);

        var missing = GitRepositoryService.IdentifyAuthenticationFailure(
            "ERROR: Repository not found.",
            "github.com");
        Assert.NotNull(missing);
        Assert.Contains("github.com", missing!, StringComparison.Ordinal);

        Assert.Null(GitRepositoryService.IdentifyAuthenticationFailure(
            "error: failed to push some refs: rejected",
            "github.com"));
        var noHost = GitRepositoryService.IdentifyAuthenticationFailure(
            "fatal: Authentication failed",
            null);
        Assert.NotNull(noHost);
        Assert.Contains("the remote", noHost!, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        DeleteDirectory(repositoryRoot);
    }

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
