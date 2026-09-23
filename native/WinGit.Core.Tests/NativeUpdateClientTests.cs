using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinGit.Core;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class NativeUpdateClientTests
{
    [Fact]
    public async Task ManualCheckBypassesStaggeringAndKeepsOnlyVerifiedDownload()
    {
        var directory = CreateDirectory();
        try
        {
            var archive = CreateArchive();
            var hash = Convert.ToHexString(SHA256.HashData(archive));
            var handler = new UpdateHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/feed")
                {
                    Assert.Contains("channel=beta", request.RequestUri.Query);
                    Assert.Contains("skipGuidCheck=1", request.RequestUri.Query);
                    Assert.Contains("guid=", request.RequestUri.Query);
                    return JsonResponse($$"""
                        {"version":"2.0.0-beta.1","channel":"beta","assetUrl":"https://updates.test/update.zip","sha256":"{{hash}}"}
                        """);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
            });
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(directory, httpClient, new TestVerifier(true));
            var states = new List<NativeUpdateStatus>();
            var downloadProgress = new List<long>();
            client.StateChanged += state =>
            {
                states.Add(state.Status);
                if (state.Status == NativeUpdateStatus.Downloading)
                {
                    downloadProgress.Add(state.DownloadedBytes);
                }
            };

            await client.CheckAsync(manual: true);

            Assert.True(client.State.Status == NativeUpdateStatus.Downloaded, client.State.Message);
            Assert.Equal(new[] { NativeUpdateStatus.Checking, NativeUpdateStatus.Downloading, NativeUpdateStatus.Downloaded },
                states.Distinct());
            Assert.Contains(archive.Length, downloadProgress);
            Assert.True(File.Exists(Path.Combine(directory, "updates", $"{hash.ToLowerInvariant()}.zip")));
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "updates")));
            Assert.True(Guid.TryParseExact(File.ReadAllText(Path.Combine(directory, ".update-id")), "D", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task InvalidHashOrUnsignedExecutableCannotBecomeDownloaded(bool validHash, bool validSignature)
    {
        var directory = CreateDirectory();
        try
        {
            var archive = CreateArchive();
            var hash = validHash ? Convert.ToHexString(SHA256.HashData(archive)) : new string('0', 64);
            var handler = new UpdateHandler(request => request.RequestUri!.AbsolutePath == "/feed"
                ? JsonResponse($$"""
                    {"version":"2.0.0-beta.1","channel":"beta","assetUrl":"https://updates.test/update.zip","sha256":"{{hash}}"}
                    """)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(directory, httpClient, new TestVerifier(validSignature));

            await client.CheckAsync(manual: false);

            Assert.Equal(NativeUpdateStatus.Failed, client.State.Status);
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "updates")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NoAvailableUpdateKeepsCheckTimeWithoutDownloading()
    {
        var directory = CreateDirectory();
        try
        {
            var handler = new UpdateHandler(request =>
            {
                Assert.DoesNotContain("skipGuidCheck", request.RequestUri!.Query);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(directory, httpClient, new TestVerifier(false));

            await client.CheckAsync(manual: false);

            Assert.Equal(NativeUpdateStatus.NotAvailable, client.State.Status);
            Assert.NotNull(client.State.LastSuccessfulCheck);
            Assert.False(Directory.Exists(Path.Combine(directory, "updates")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BlockedSigningGateCannotBecomeDownloaded()
    {
        var directory = CreateDirectory();
        try
        {
            var archive = CreateArchive(releaseGate: "Blocked");
            var hash = Convert.ToHexString(SHA256.HashData(archive));
            var handler = new UpdateHandler(request => request.RequestUri!.AbsolutePath == "/feed"
                ? JsonResponse($$"""
                    {"version":"2.0.0-beta.1","channel":"beta","assetUrl":"https://updates.test/update.zip","sha256":"{{hash}}"}
                    """)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(directory, httpClient, new TestVerifier(true));

            await client.CheckAsync(manual: false);

            Assert.Equal(NativeUpdateStatus.Failed, client.State.Status);
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "updates")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task OnlyCompleteVerifiedArchiveCanBeStagedWithoutReplacingCurrentApp(bool installable, bool canStage)
    {
        var directory = CreateDirectory();
        try
        {
            var target = Path.Combine(directory, "current-app");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "WinGit.Native.exe"), "existing installation");
            var archive = CreateArchive(installable: installable);
            var hash = Convert.ToHexString(SHA256.HashData(archive));
            using var httpClient = new HttpClient(new UpdateHandler(request => request.RequestUri!.AbsolutePath == "/feed"
                ? JsonResponse($$"""
                    {"version":"2.0.0-beta.1","channel":"beta","assetUrl":"https://updates.test/update.zip","sha256":"{{hash}}"}
                    """)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
            var client = CreateClient(directory, httpClient, new TestVerifier(true));
            await client.CheckAsync(manual: true);
            Assert.Equal(NativeUpdateStatus.Downloaded, client.State.Status);

            var plan = await client.PrepareInstallationAsync(target);

            Assert.Equal(canStage, plan is not null);
            Assert.Equal("existing installation", File.ReadAllText(Path.Combine(target, "WinGit.Native.exe")));
            if (plan is not null)
            {
                Assert.Equal(hash, plan.ArchiveSha256);
                Assert.True(File.Exists(Path.Combine(plan.StagedDirectory, "WinGit.Native.exe")));
                Directory.Delete(plan.StagedDirectory, recursive: true);
            }
            else
            {
                Assert.Equal(NativeUpdateStatus.Failed, client.State.Status);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallCannotStartWithoutVerifiedDownload()
    {
        var directory = CreateDirectory();
        try
        {
            var target = Path.Combine(directory, "current-app");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "WinGit.Native.exe"), "existing installation");
            using var httpClient = new HttpClient(new UpdateHandler(_ => throw new InvalidOperationException("No download expected")));
            var client = CreateClient(directory, httpClient, new TestVerifier(true));

            Assert.Null(await client.PrepareInstallationAsync(target));
            Assert.Equal(NativeUpdateStatus.Failed, client.State.Status);
            Assert.Equal("existing installation", File.ReadAllText(Path.Combine(target, "WinGit.Native.exe")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedDownloadedArchiveCannotBeStaged()
    {
        var directory = CreateDirectory();
        try
        {
            var target = Path.Combine(directory, "current-app");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "WinGit.Native.exe"), "existing installation");
            var archive = CreateArchive(installable: true);
            var hash = Convert.ToHexString(SHA256.HashData(archive));
            using var httpClient = new HttpClient(new UpdateHandler(request => request.RequestUri!.AbsolutePath == "/feed"
                ? JsonResponse($$"""
                    {"version":"2.0.0-beta.1","channel":"beta","assetUrl":"https://updates.test/update.zip","sha256":"{{hash}}"}
                    """)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
            var client = CreateClient(directory, httpClient, new TestVerifier(true));
            await client.CheckAsync(manual: true);
            File.WriteAllText(Path.Combine(directory, "updates", $"{hash.ToLowerInvariant()}.zip"), "changed archive");

            var plan = await client.PrepareInstallationAsync(target);

            Assert.Null(plan);
            Assert.Equal(NativeUpdateStatus.Failed, client.State.Status);
            Assert.Equal("existing installation", File.ReadAllText(Path.Combine(target, "WinGit.Native.exe")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackageWithoutValidCatalogCannotBeStaged()
    {
        var directory = CreateDirectory();
        try
        {
            var target = Path.Combine(directory, "current-app");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "WinGit.Native.exe"), "existing installation");
            var archive = CreateArchive(installable: true);
            var hash = Convert.ToHexString(SHA256.HashData(archive));
            using var httpClient = new HttpClient(new UpdateHandler(request => request.RequestUri!.AbsolutePath == "/feed"
                ? JsonResponse($$"""
                    {"version":"2.0.0-beta.1","channel":"beta","assetUrl":"https://updates.test/update.zip","sha256":"{{hash}}"}
                    """)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
            var client = CreateClient(directory, httpClient, new TestVerifier(true, packageValid: false));
            await client.CheckAsync(manual: true);

            Assert.Null(await client.PrepareInstallationAsync(target));
            Assert.Equal(NativeUpdateStatus.Failed, client.State.Status);
            Assert.Equal("existing installation", File.ReadAllText(Path.Combine(target, "WinGit.Native.exe")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static NativeUpdateClient CreateClient(string directory, HttpClient httpClient, INativeUpdateSignatureVerifier verifier) =>
        new(httpClient, verifier, new NativeUpdateOptions(
            new Uri("https://updates.test/feed"), "beta", "CN=Test Signer", "1.0.0",
            Path.Combine(directory, "updates"), Path.Combine(directory, ".update-id")));

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wingit-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] CreateArchive(string releaseGate = "Passed", bool installable = false)
    {
        var executableBytes = Encoding.UTF8.GetBytes("synthetic signed executable");
        var executableHash = Convert.ToHexString(SHA256.HashData(executableBytes));
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var executable = archive.CreateEntry("WinGit.Native.exe").Open())
            {
                executable.Write(executableBytes);
            }

            using (var signingStatus = new StreamWriter(archive.CreateEntry("SigningStatus.json").Open()))
            {
                signingStatus.Write($$"""
                    {"artifact":"WinGit.Native.exe","sha256":"{{executableHash}}","signatureStatus":"Valid","signerSubject":"CN=Test Signer","releaseGate":"{{releaseGate}}"}
                    """);
            }

            foreach (var path in new[] { "App.xbf", "MainWindow.xbf", "WinGit.Native.pri" })
            {
                using var requiredFile = archive.CreateEntry(path).Open();
                requiredFile.WriteByte(1);
            }
            if (installable)
            {
                foreach (var path in new[]
                {
                    "WinGit.Native.dll", "WinGit.Core.dll", "WinGit.Native.deps.json",
                    "WinGit.Native.runtimeconfig.json", "NativeImageDiffView.xbf", "NativeSubmoduleDiffView.xbf",
                    "Assets/icon-logo.ico", "ReleaseNotes.txt", "Acknowledgements.txt", "LICENSE.txt",
                    "git/LICENSE.txt", "git/dugite-LICENSE", "git/cmd/git.exe",
                    "git/mingw64/bin/git.exe", "git/mingw64/libexec/git-core/git-lfs.exe",
                    "git/mingw64/libexec/git-core/git-credential-wincred.exe", "git/usr/bin/sh.exe",
                    "verify-update-signature.ps1", "verify-update-package.ps1",
                    "apply-native-update.ps1", "UpdateCatalog.cat"
                })
                {
                    using var requiredFile = archive.CreateEntry(path).Open();
                    requiredFile.WriteByte(1);
                }
            }
        }

        return stream.ToArray();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class UpdateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class TestVerifier(bool valid, bool packageValid = true) : INativeUpdateSignatureVerifier
    {
        public Task<bool> IsValidAsync(string executablePath, string expectedSignerSubject, CancellationToken cancellationToken)
        {
            Assert.Equal("CN=Test Signer", expectedSignerSubject);
            Assert.True(File.Exists(executablePath));
            return Task.FromResult(valid);
        }

        public Task<bool> IsPackageValidAsync(string packageDirectory, string expectedSignerSubject, CancellationToken cancellationToken)
        {
            Assert.Equal("CN=Test Signer", expectedSignerSubject);
            Assert.True(File.Exists(Path.Combine(packageDirectory, "UpdateCatalog.cat")));
            return Task.FromResult(packageValid);
        }
    }
}
