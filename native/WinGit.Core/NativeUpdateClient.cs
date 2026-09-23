using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinGit.Core;

public enum NativeUpdateStatus
{
    NotChecked,
    Checking,
    NotAvailable,
    Downloading,
    Downloaded,
    Installing,
    Failed
}

public sealed record NativeUpdateState(
    NativeUpdateStatus Status,
    string Message,
    DateTimeOffset? LastSuccessfulCheck = null,
    long DownloadedBytes = 0,
    long? TotalBytes = null,
    string? Version = null);

public sealed record NativeUpdateOptions(
    Uri FeedUrl,
    string Channel,
    string ExpectedSignerSubject,
    string CurrentVersion,
    string DownloadDirectory,
    string UpdaterIdPath);

public interface INativeUpdateSignatureVerifier
{
    Task<bool> IsValidAsync(string executablePath, string expectedSignerSubject, CancellationToken cancellationToken);
    Task<bool> IsPackageValidAsync(string packageDirectory, string expectedSignerSubject, CancellationToken cancellationToken);
}

public sealed record NativeUpdateInstallPlan(
    string ArchivePath,
    string ArchiveSha256,
    string StagedDirectory,
    string InstallationDirectory,
    string ExpectedSignerSubject);

public sealed class NativeUpdateClient
{
    private const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    private const long MaximumExtractedBytes = 4L * 1024 * 1024 * 1024;
    private static readonly Regex VersionPattern = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<label>beta|test)\.(?<revision>0|[1-9]\d*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HttpClient httpClient;
    private readonly INativeUpdateSignatureVerifier signatureVerifier;
    private readonly NativeUpdateOptions options;
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private (string Path, string Sha256)? verifiedArchive;

    public NativeUpdateState State { get; private set; } = new(NativeUpdateStatus.NotChecked, "Updates have not been checked.");
    public event Action<NativeUpdateState>? StateChanged;

    public NativeUpdateClient(HttpClient httpClient, INativeUpdateSignatureVerifier signatureVerifier, NativeUpdateOptions options)
    {
        if (!IsHttpsUrl(options.FeedUrl)
            || options.Channel is not ("production" or "beta" or "test")
            || string.IsNullOrWhiteSpace(options.ExpectedSignerSubject)
            || !VersionPattern.IsMatch(options.CurrentVersion)
            || !Path.IsPathFullyQualified(options.DownloadDirectory)
            || !Path.IsPathFullyQualified(options.UpdaterIdPath))
        {
            throw new ArgumentException("Native update configuration is invalid.", nameof(options));
        }

        this.httpClient = httpClient;
        this.signatureVerifier = signatureVerifier;
        this.options = options;
    }

    public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (!await checkGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            if (State.Status is NativeUpdateStatus.Downloaded)
            {
                return;
            }

            verifiedArchive = null;
            SetState(new(NativeUpdateStatus.Checking, "Checking for updates...", State.LastSuccessfulCheck));
            var requestUrl = await CreateRequestUrlAsync(manual, cancellationToken);
            using var response = await httpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            {
                SetState(new(NativeUpdateStatus.NotAvailable, "You have the latest version.", DateTimeOffset.UtcNow));
                return;
            }

            response.EnsureSuccessStatusCode();
            await using var manifestStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(
                manifestStream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken)
                ?? throw new InvalidDataException("Update metadata is empty.");
            if (manifest.Channel != options.Channel || !TryCompareVersions(manifest.Version, options.CurrentVersion, out var newer))
            {
                throw new InvalidDataException("Update version or channel is invalid.");
            }

            if (!newer)
            {
                SetState(new(NativeUpdateStatus.NotAvailable, "You have the latest version.", DateTimeOffset.UtcNow));
                return;
            }

            if (options.Channel == "production" && manifest.Version!.Contains('-'))
            {
                throw new InvalidDataException("Production update metadata contains a prerelease version.");
            }

            if (manifest.Sha256 is null || manifest.Sha256.Length != 64
                || !manifest.Sha256.All(Uri.IsHexDigit)
                || !Uri.TryCreate(manifest.AssetUrl, UriKind.Absolute, out var assetUrl)
                || !IsHttpsUrl(assetUrl)
                || !string.Equals(assetUrl.Host, options.FeedUrl.Host, StringComparison.OrdinalIgnoreCase)
                || assetUrl.Port != options.FeedUrl.Port)
            {
                throw new InvalidDataException("Update download metadata is invalid.");
            }

            var checkedAt = DateTimeOffset.UtcNow;
            SetState(new(NativeUpdateStatus.Downloading, "Downloading update...", checkedAt, Version: manifest.Version));
            await DownloadAndValidateAsync(assetUrl, manifest, checkedAt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(new(NativeUpdateStatus.NotChecked, "Update check cancelled.", State.LastSuccessfulCheck));
        }
        catch (OperationCanceledException)
        {
            SetState(new(NativeUpdateStatus.Failed, "Update check timed out.", State.LastSuccessfulCheck));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetState(new(NativeUpdateStatus.Failed, $"Update check failed: {exception.Message}", State.LastSuccessfulCheck));
        }
        finally
        {
            checkGate.Release();
        }
    }

    private async Task DownloadAndValidateAsync(Uri assetUrl, UpdateManifest manifest, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DownloadDirectory);
        var partialPath = Path.Combine(options.DownloadDirectory, $"{Guid.NewGuid():N}.partial");
        var verifiedPath = Path.Combine(options.DownloadDirectory, $"{manifest.Sha256!.ToLowerInvariant()}.zip");
        try
        {
            using var response = await httpClient.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is > MaximumArchiveBytes)
            {
                throw new InvalidDataException("Update archive exceeds the size limit.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    downloaded += count;
                    if (downloaded > MaximumArchiveBytes)
                    {
                        throw new InvalidDataException("Update archive exceeds the size limit.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    SetState(new(NativeUpdateStatus.Downloading, "Downloading update...", checkedAt, downloaded, totalBytes, manifest.Version));
                }
            }

            string hash;
            await using (var hashStream = File.OpenRead(partialPath))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
            }
            if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Update archive hash does not match metadata.");
            }

            using (var archive = ZipFile.OpenRead(partialPath))
            {
                if (archive.Entries.Any(entry => !IsSafeArchivePath(entry.FullName))
                    || archive.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
                    || archive.GetEntry("WinGit.Native.exe") is null
                    || archive.GetEntry("SigningStatus.json") is null
                    || archive.GetEntry("App.xbf") is null
                    || archive.GetEntry("MainWindow.xbf") is null
                    || archive.GetEntry("WinGit.Native.pri") is null)
                {
                    throw new InvalidDataException("Update archive is missing required files or has invalid paths.");
                }

                var executable = archive.GetEntry("WinGit.Native.exe")!;
                var signingStatusEntry = archive.GetEntry("SigningStatus.json")!;
                if (executable.Length is <= 0 or > 100_000_000)
                {
                    throw new InvalidDataException("Update executable size is invalid.");
                }
                if (signingStatusEntry.Length is <= 0 or > 16_384)
                {
                    throw new InvalidDataException("Update signing evidence size is invalid.");
                }
                var executablePath = Path.Combine(options.DownloadDirectory, $"{Guid.NewGuid():N}.exe");
                try
                {
                    executable.ExtractToFile(executablePath);
                    await using (var signingStatusStream = signingStatusEntry.Open())
                    {
                        var signingStatus = await JsonSerializer.DeserializeAsync<SigningStatus>(
                            signingStatusStream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
                        await using var executableStream = File.OpenRead(executablePath);
                        var executableHash = Convert.ToHexString(await SHA256.HashDataAsync(executableStream, cancellationToken));
                        if (signingStatus?.Artifact != "WinGit.Native.exe"
                            || signingStatus.ReleaseGate != "Passed"
                            || signingStatus.SignatureStatus != "Valid"
                            || signingStatus.SignerSubject != options.ExpectedSignerSubject
                            || !executableHash.Equals(signingStatus.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Update signing evidence is invalid or blocked.");
                        }
                    }

                    if (!await signatureVerifier.IsValidAsync(executablePath, options.ExpectedSignerSubject, cancellationToken))
                    {
                        throw new InvalidDataException("Update executable has no valid signature from the expected signer.");
                    }
                }
                finally
                {
                    File.Delete(executablePath);
                }
            }

            File.Move(partialPath, verifiedPath, true);
            verifiedArchive = (verifiedPath, manifest.Sha256.ToUpperInvariant());
            SetState(new(NativeUpdateStatus.Downloaded, "Update downloaded. Package verification runs before installation.", checkedAt, Version: manifest.Version));
        }
        finally
        {
            File.Delete(partialPath);
        }
    }

    public async Task<NativeUpdateInstallPlan?> PrepareInstallationAsync(
        string installationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!await checkGate.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        string? stagedDirectory = null;
        try
        {
            if (State.Status != NativeUpdateStatus.Downloaded || verifiedArchive is not { } downloaded)
            {
                SetState(new(NativeUpdateStatus.Failed, "No verified update is ready to install.", State.LastSuccessfulCheck));
                return null;
            }

            var fullTarget = Path.GetFullPath(installationDirectory);
            var target = fullTarget.TrimEnd(Path.DirectorySeparatorChar);
            if (!Path.IsPathFullyQualified(installationDirectory)
                || Path.GetPathRoot(fullTarget) == fullTarget
                || !File.Exists(Path.Combine(target, "WinGit.Native.exe")))
            {
                throw new InvalidDataException("The current installation directory is invalid.");
            }

            SetState(new(NativeUpdateStatus.Installing, "Verifying the downloaded update before installation...",
                State.LastSuccessfulCheck, Version: State.Version));
            await using (var archiveStream = File.OpenRead(downloaded.Path))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellationToken));
                if (!actualHash.Equals(downloaded.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The downloaded update changed after verification.");
                }
            }

            var parent = Path.GetDirectoryName(target)!;
            stagedDirectory = Path.Combine(parent, $".{Path.GetFileName(target)}.update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagedDirectory);
            using (var archive = ZipFile.OpenRead(downloaded.Path))
            {
                ValidateInstallArchive(archive);
                var totalBytes = archive.Entries.Sum(entry => entry.Length);
                long extractedBytes = 0;
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.Combine(stagedDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var source = entry.Open();
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920];
                    long fileBytes = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        fileBytes += count;
                        extractedBytes += count;
                        if (fileBytes > entry.Length || extractedBytes > MaximumExtractedBytes)
                        {
                            throw new InvalidDataException("The update archive exceeds its declared size.");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                    if (fileBytes != entry.Length)
                    {
                        throw new InvalidDataException("The update archive has an incomplete file.");
                    }
                    SetState(new(NativeUpdateStatus.Installing, "Preparing update files...",
                        State.LastSuccessfulCheck, extractedBytes, totalBytes, State.Version));
                }
            }

            var executablePath = Path.Combine(stagedDirectory, "WinGit.Native.exe");
            await using (var signingStream = File.OpenRead(Path.Combine(stagedDirectory, "SigningStatus.json")))
            {
                var signingStatus = await JsonSerializer.DeserializeAsync<SigningStatus>(
                    signingStream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
                await using var executableStream = File.OpenRead(executablePath);
                var executableHash = Convert.ToHexString(await SHA256.HashDataAsync(executableStream, cancellationToken));
                if (signingStatus?.Artifact != "WinGit.Native.exe"
                    || signingStatus.ReleaseGate != "Passed"
                    || signingStatus.SignatureStatus != "Valid"
                    || signingStatus.SignerSubject != options.ExpectedSignerSubject
                    || !executableHash.Equals(signingStatus.Sha256, StringComparison.OrdinalIgnoreCase)
                    || !await signatureVerifier.IsValidAsync(executablePath, options.ExpectedSignerSubject, cancellationToken))
                {
                    throw new InvalidDataException("The staged update has invalid signing evidence or signature.");
                }
            }

            if (!await signatureVerifier.IsPackageValidAsync(stagedDirectory, options.ExpectedSignerSubject, cancellationToken))
            {
                throw new InvalidDataException("The staged update package has no valid signed file catalog.");
            }

            return new NativeUpdateInstallPlan(downloaded.Path, downloaded.Sha256, stagedDirectory, target,
                options.ExpectedSignerSubject);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemoveStagedDirectory(stagedDirectory);
            SetState(new(NativeUpdateStatus.Failed, "Update installation was cancelled before any files were replaced.", State.LastSuccessfulCheck));
            return null;
        }
        catch (Exception exception)
        {
            RemoveStagedDirectory(stagedDirectory);
            SetState(new(NativeUpdateStatus.Failed, $"Update installation could not start: {exception.Message}", State.LastSuccessfulCheck));
            return null;
        }
        finally
        {
            checkGate.Release();
        }
    }

    public void ReportInstallHandoffFailure(NativeUpdateInstallPlan plan)
    {
        RemoveStagedDirectory(plan.StagedDirectory);
        SetState(new(NativeUpdateStatus.Failed, "Update installation could not start. The current installation is unchanged.", State.LastSuccessfulCheck));
    }

    private static void RemoveStagedDirectory(string? stagedDirectory)
    {
        if (stagedDirectory is not null && Directory.Exists(stagedDirectory))
        {
            try
            {
                Directory.Delete(stagedDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ValidateInstallArchive(ZipArchive archive)
    {
        string[] requiredFiles =
        [
            "WinGit.Native.exe", "WinGit.Native.dll", "WinGit.Core.dll",
            "WinGit.Native.deps.json", "WinGit.Native.runtimeconfig.json", "SigningStatus.json",
            "App.xbf", "MainWindow.xbf",
            "NativeImageDiffView.xbf", "NativeSubmoduleDiffView.xbf", "WinGit.Native.pri",
            "Assets/icon-logo.ico", "ReleaseNotes.txt", "Acknowledgements.txt", "LICENSE.txt",
            "codex/codex-LICENSE.txt", "codex/package.json",
            "codex/vendor/x86_64-pc-windows-msvc/bin/codex.exe",
            "codex/vendor/x86_64-pc-windows-msvc/bin/codex-code-mode-host.exe",
            "codex/vendor/x86_64-pc-windows-msvc/codex-path/rg.exe",
            "codex/vendor/x86_64-pc-windows-msvc/codex-resources/codex-command-runner.exe",
            "codex/vendor/x86_64-pc-windows-msvc/codex-resources/codex-windows-sandbox-setup.exe",
            "git/LICENSE.txt", "git/dugite-LICENSE", "git/cmd/git.exe",
            "git/mingw64/bin/git.exe", "git/mingw64/libexec/git-core/git-lfs.exe",
            "git/mingw64/libexec/git-core/git-credential-wincred.exe", "git/usr/bin/sh.exe",
            "verify-update-signature.ps1", "verify-update-package.ps1",
            "apply-native-update.ps1", "UpdateCatalog.cat"
        ];
        var entries = archive.Entries;
        if (entries.Count > 10_000
            || entries.Any(entry => !IsSafeArchivePath(entry.FullName)
                || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
            || entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || entries.Sum(entry => entry.Length) > MaximumExtractedBytes
            || requiredFiles.Any(path => archive.GetEntry(path) is not { Length: > 0 }))
        {
            throw new InvalidDataException("The update archive is incomplete or has invalid entries.");
        }
    }

    private async Task<Uri> CreateRequestUrlAsync(bool manual, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.UpdaterIdPath)!);
        var updaterId = File.Exists(options.UpdaterIdPath)
            ? (await File.ReadAllTextAsync(options.UpdaterIdPath, cancellationToken)).Trim()
            : string.Empty;
        if (!Guid.TryParseExact(updaterId, "D", out _))
        {
            updaterId = Guid.NewGuid().ToString("D");
            await File.WriteAllTextAsync(options.UpdaterIdPath, updaterId, cancellationToken);
        }

        var builder = new UriBuilder(options.FeedUrl);
        var query = builder.Query.TrimStart('?');
        builder.Query = string.Join('&', new[]
        {
            query,
            $"channel={Uri.EscapeDataString(options.Channel)}",
            $"version={Uri.EscapeDataString(options.CurrentVersion)}",
            $"guid={updaterId}",
            manual ? "skipGuidCheck=1" : string.Empty
        }.Where(value => value.Length > 0));
        return builder.Uri;
    }

    private static bool IsHttpsUrl(Uri url) => url.IsAbsoluteUri
        && url.Scheme == Uri.UriSchemeHttps
        && url.UserInfo.Length == 0
        && url.Fragment.Length == 0;

    private static bool IsSafeArchivePath(string path) => path.Length > 0
        && !path.StartsWith('/')
        && !path.Contains('\\')
        && !path.Contains(':')
        && !path.TrimEnd('/').Split('/').Any(part => part is "" or "." or "..");

    private static bool TryCompareVersions(string? candidate, string current, out bool newer)
    {
        newer = false;
        var candidateMatch = VersionPattern.Match(candidate ?? string.Empty);
        var currentMatch = VersionPattern.Match(current);
        if (!candidateMatch.Success || !currentMatch.Success)
        {
            return false;
        }

        foreach (var part in new[] { "major", "minor", "patch" })
        {
            if (!long.TryParse(candidateMatch.Groups[part].Value, out var candidatePart)
                || !long.TryParse(currentMatch.Groups[part].Value, out var currentPart))
            {
                return false;
            }

            if (candidatePart != currentPart)
            {
                newer = candidatePart > currentPart;
                return true;
            }
        }

        var candidateLabel = candidateMatch.Groups["label"].Value;
        var currentLabel = currentMatch.Groups["label"].Value;
        if (candidateLabel != currentLabel)
        {
            newer = candidateLabel.Length == 0 || currentLabel == "test" && candidateLabel == "beta";
            return true;
        }

        newer = candidateLabel.Length > 0
            && long.TryParse(candidateMatch.Groups["revision"].Value, out var candidateRevision)
            && long.TryParse(currentMatch.Groups["revision"].Value, out var currentRevision)
            && candidateRevision > currentRevision;
        return true;
    }

    private void SetState(NativeUpdateState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private sealed record UpdateManifest(string? Version, string? Channel, string? AssetUrl, string? Sha256);
    private sealed record SigningStatus(string? Artifact, string? Sha256, string? SignatureStatus, string? SignerSubject, string? ReleaseGate);
}
