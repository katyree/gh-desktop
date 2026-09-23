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

    private static byte[] CreateArchive(string releaseGate = "Passed")
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

    private sealed class TestVerifier(bool valid) : INativeUpdateSignatureVerifier
    {
        public Task<bool> IsValidAsync(string executablePath, string expectedSignerSubject, CancellationToken cancellationToken)
        {
            Assert.Equal("CN=Test Signer", expectedSignerSubject);
            Assert.True(File.Exists(executablePath));
            return Task.FromResult(valid);
        }
    }
}
