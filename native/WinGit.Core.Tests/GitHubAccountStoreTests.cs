using System.Text;
using WinGit.Core.GitHub;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class GitHubAccountStoreTests
{
    [Fact]
    public async Task RoundTripUpsertRemoveAndClearPreserveOtherAccounts()
    {
        var fixtureRoot = CreateFixtureRoot();
        try
        {
            var filePath = Path.Combine(fixtureRoot, "accounts.dat");
            var siblingPath = Path.Combine(fixtureRoot, "keep.txt");
            File.WriteAllText(siblingPath, "preserve this file");
            var dotComOrigin = GitHubApiEndpoint.GitHubCom;
            var enterpriseOrigin = GitHubApiEndpoint.ForEnterprise(
                new Uri("https://api.company.test"));
            var firstProfile = CreateProfile(
                dotComOrigin,
                101,
                "first-user",
                "First User",
                "first-user@example.invalid",
                new Uri("https://avatars.githubusercontent.com/u/101"),
                new Uri("https://github.com/first-user"));
            var secondProfile = CreateProfile(
                enterpriseOrigin,
                202,
                "second-user",
                null,
                null,
                null,
                new Uri("https://api.company.test/second-user"));
            var firstSession = CreateSession(
                "first-token",
                new Uri("https://github.com"));
            var secondSession = CreateSession(
                "second-token",
                new Uri("https://api.company.test"));

            using var store = new GitHubAccountStore(filePath);
            await store.SaveAsync(firstProfile, firstSession);
            await store.SaveAsync(secondProfile, secondSession);

            var thirdProfile = CreateProfile(
                dotComOrigin,
                404,
                "third-user",
                "Third User",
                null,
                null,
                new Uri("https://github.com/third-user"));
            var thirdSession = CreateSession(
                "third-token",
                new Uri("https://github.com"));
            var concurrentPath = Path.Combine(
                fixtureRoot,
                "concurrent.dat");
            using (var seedStore = new GitHubAccountStore(concurrentPath))
            {
                await seedStore.SaveAsync(firstProfile, firstSession);
            }

            using (var firstConcurrentStore = new GitHubAccountStore(concurrentPath))
            using (var secondConcurrentStore = new GitHubAccountStore(concurrentPath))
            {
                await Task.WhenAll(
                    Task.Run(() => firstConcurrentStore.SaveAsync(
                        secondProfile,
                        secondSession)),
                    Task.Run(() => secondConcurrentStore.SaveAsync(
                        thirdProfile,
                        thirdSession)));
            }

            using var concurrentReadStore = new GitHubAccountStore(concurrentPath);
            var concurrentSummaries = await concurrentReadStore.ListAsync();
            Assert.Equal(3, concurrentSummaries.Count);
            var restoredThird = await concurrentReadStore.LoadAsync(
                dotComOrigin,
                404);
            Assert.NotNull(restoredThird);
            Assert.Equal("third-token", restoredThird!.Session.AccessToken);

            var mismatchedSession = CreateSession(
                "mismatch-token",
                new Uri("https://api.company.test"));
            var mismatch = await Assert.ThrowsAsync<GitHubAccountStoreException>(
                () => store.SaveAsync(firstProfile, mismatchedSession));
            Assert.Equal(
                GitHubAccountStoreErrorKind.InvalidConfiguration,
                mismatch.Kind);
            Assert.Equal(2, (await store.ListAsync()).Count);

            var encryptedBytes = File.ReadAllBytes(filePath);
            var encryptedText = Encoding.UTF8.GetString(encryptedBytes);
            Assert.DoesNotContain("first-token", encryptedText, StringComparison.Ordinal);
            Assert.DoesNotContain("second-token", encryptedText, StringComparison.Ordinal);
            Assert.DoesNotContain("First User", encryptedText, StringComparison.Ordinal);

            var summaries = await store.ListAsync();
            Assert.Equal(2, summaries.Count);
            Assert.Equal(
                [101L, 202L],
                summaries.Select(summary => summary.Id).ToArray());
            Assert.DoesNotContain(
                summaries.Select(summary => summary.ToString()),
                summary => summary.Contains("token", StringComparison.OrdinalIgnoreCase));

            var loadedFirst = await store.LoadAsync(dotComOrigin, 101);
            Assert.NotNull(loadedFirst);
            Assert.Equal("first-user", loadedFirst!.Profile.Login);
            Assert.Equal(dotComOrigin, loadedFirst.Profile.ApiOrigin);
            Assert.Equal(dotComOrigin, loadedFirst.Session.ApiOrigin);
            Assert.DoesNotContain("first-token", loadedFirst.Session.ToString());

            var updatedProfile = CreateProfile(
                dotComOrigin,
                101,
                "first-user-renamed",
                "Updated User",
                null,
                null,
                new Uri("https://github.com/first-user-renamed"));
            var updatedSession = CreateSession(
                "updated-token",
                new Uri("https://github.com"));
            await store.SaveAsync(updatedProfile, updatedSession);

            var updated = await store.LoadAsync(dotComOrigin, 101);
            Assert.NotNull(updated);
            Assert.Equal("first-user-renamed", updated!.Profile.Login);
            Assert.Equal("Updated User", updated.Profile.DisplayName);
            Assert.Equal(2, (await store.ListAsync()).Count);

            Assert.True(await store.RemoveAsync(dotComOrigin, 101));
            Assert.False(await store.RemoveAsync(dotComOrigin, 101));
            var afterRemove = await store.ListAsync();
            var remaining = Assert.Single(afterRemove);
            Assert.Equal(202, remaining.Id);
            Assert.Equal(enterpriseOrigin, remaining.ApiOrigin);
            Assert.Equal("preserve this file", File.ReadAllText(siblingPath));

            await store.ClearAsync();
            Assert.Empty(await store.ListAsync());
            Assert.True(File.Exists(filePath));
            Assert.Equal("preserve this file", File.ReadAllText(siblingPath));
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    [Fact]
    public async Task CorruptPayloadIsPreservedAndCancelledSaveDoesNotWrite()
    {
        var fixtureRoot = CreateFixtureRoot();
        try
        {
            var filePath = Path.Combine(fixtureRoot, "accounts.dat");
            var profile = CreateProfile(
                GitHubApiEndpoint.GitHubCom,
                303,
                "corrupt-test-user",
                "Corrupt Test User",
                null,
                null,
                new Uri("https://github.com/corrupt-test-user"));
            var session = CreateSession(
                "corrupt-test-token",
                new Uri("https://github.com"));

            using (var validStore = new GitHubAccountStore(filePath))
            {
                await validStore.SaveAsync(profile, session);
            }

            var originalBytes = File.ReadAllBytes(filePath);
            var corruptBytes = originalBytes.ToArray();
            corruptBytes[0] ^= 0x01;
            File.WriteAllBytes(filePath, corruptBytes);

            using (var corruptStore = new GitHubAccountStore(filePath))
            {
                var failure = await Assert.ThrowsAsync<GitHubAccountStoreException>(
                    () => corruptStore.ListAsync());
                Assert.Equal(
                    GitHubAccountStoreErrorKind.ProtectionUnavailable,
                    failure.Kind);
                Assert.DoesNotContain("corrupt-test-token", failure.ToString());
                Assert.Equal(corruptBytes, File.ReadAllBytes(filePath));

                var saveFailure = await Assert.ThrowsAsync<GitHubAccountStoreException>(
                    () => corruptStore.SaveAsync(profile, session));
                Assert.Equal(
                    GitHubAccountStoreErrorKind.ProtectionUnavailable,
                    saveFailure.Kind);
                Assert.Equal(corruptBytes, File.ReadAllBytes(filePath));

                var clearFailure = await Assert.ThrowsAsync<GitHubAccountStoreException>(
                    () => corruptStore.ClearAsync());
                Assert.Equal(
                    GitHubAccountStoreErrorKind.ProtectionUnavailable,
                    clearFailure.Kind);
                Assert.Equal(corruptBytes, File.ReadAllBytes(filePath));
            }

            var cancelledPath = Path.Combine(fixtureRoot, "cancelled.dat");
            using var cancelledStore = new GitHubAccountStore(cancelledPath);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelledStore.SaveAsync(
                    profile,
                    session,
                    cancellation.Token));
            Assert.False(File.Exists(cancelledPath));
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    private static GitHubAuthenticatedProfile CreateProfile(
        Uri apiOrigin,
        long id,
        string login,
        string? displayName,
        string? email,
        Uri? avatarUrl,
        Uri profileUrl) =>
        new(
            apiOrigin,
            id,
            login,
            displayName,
            email,
            avatarUrl,
            profileUrl);

    private static GitHubAccountSession CreateSession(
        string accessToken,
        Uri issuerHost) =>
        GitHubAccountSession.FromDeviceToken(
            new GitHubDeviceAccessToken(
                accessToken,
                "bearer",
                "repo user workflow",
                issuerHost));

    private static string CreateFixtureRoot()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var fixtureRoot = Path.Combine(
            tempRoot,
            "WinGit.Core.Tests",
            "GitHubAccountStore",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        return fixtureRoot;
    }

    private static void DeleteFixtureRoot(string fixtureRoot)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(fixtureRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullRoot.Length <= tempRoot.Length ||
            !fullRoot.StartsWith(
                tempRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(fullRoot))
        {
            Directory.Delete(fullRoot, recursive: true);
        }
    }
}
