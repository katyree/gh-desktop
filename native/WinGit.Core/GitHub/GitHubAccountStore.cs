using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinGit.Core.GitHub;

/// <summary>Sanitized failures from the native GitHub account store.</summary>
public enum GitHubAccountStoreErrorKind
{
    InvalidConfiguration,
    StorageUnavailable,
    Corrupt,
    ProtectionUnavailable,
    PayloadTooLarge,
}

/// <summary>
/// A fixed, user-safe account-store failure. File paths, payloads, and tokens
/// are deliberately excluded from its diagnostics.
/// </summary>
public sealed class GitHubAccountStoreException : InvalidOperationException
{
    public GitHubAccountStoreException(GitHubAccountStoreErrorKind kind)
        : base(GetMessage(kind))
    {
        Kind = kind;
    }

    public GitHubAccountStoreErrorKind Kind { get; }

    private static string GetMessage(GitHubAccountStoreErrorKind kind) =>
        kind switch
        {
            GitHubAccountStoreErrorKind.InvalidConfiguration =>
                "The native GitHub account store is not configured.",
            GitHubAccountStoreErrorKind.StorageUnavailable =>
                "The native GitHub account store could not be read or written.",
            GitHubAccountStoreErrorKind.Corrupt =>
                "The native GitHub account store is corrupt and was preserved.",
            GitHubAccountStoreErrorKind.ProtectionUnavailable =>
                "The native GitHub account store cannot be decrypted for this Windows user.",
            GitHubAccountStoreErrorKind.PayloadTooLarge =>
                "The native GitHub account store exceeds its size limit.",
            _ => "The native GitHub account store could not be used.",
        };
}

/// <summary>
/// The non-secret account fields shown in account pickers and settings.
/// </summary>
public sealed class GitHubAccountSummary
{
    internal GitHubAccountSummary(GitHubAuthenticatedProfile profile)
    {
        ApiOrigin = profile.ApiOrigin;
        Id = profile.Id;
        Login = profile.Login;
        DisplayName = profile.DisplayName;
        Email = profile.Email;
        AvatarUrl = profile.AvatarUrl;
        ProfileUrl = profile.ProfileUrl;
    }

    public Uri ApiOrigin { get; }

    public long Id { get; }

    public string Login { get; }

    public string? DisplayName { get; }

    public string? Email { get; }

    public Uri? AvatarUrl { get; }

    public Uri ProfileUrl { get; }

    public override string ToString() => "GitHub account summary.";
}

/// <summary>
/// A persisted account and its host-bound in-memory session. The session
/// exposes no token value and can be passed directly to the profile client.
/// </summary>
public sealed class GitHubStoredAccount
{
    internal GitHubStoredAccount(
        GitHubAuthenticatedProfile profile,
        GitHubAccountSession session)
    {
        Profile = profile;
        Session = session;
    }

    public GitHubAuthenticatedProfile Profile { get; }

    public GitHubAccountSession Session { get; }

    public GitHubAccountSummary Summary => new(Profile);

    public override string ToString() => "GitHub stored account.";
}

/// <summary>
/// Stores only native GitHub account records at the caller-supplied path.
/// Payloads are protected for the current Windows user with DPAPI. Operations
/// reread the latest file while holding this instance's update gate; mutations
/// also hold an adjacent exclusive lock for other processes or store instances.
/// </summary>
public sealed class GitHubAccountStore : IDisposable
{
    private const string FileFormat = "WinGit.Native.GitHubAccounts";
    private const int FileVersion = 1;
    private const int MaximumAccounts = 100;
    private const int MaximumLoginLength = 256;
    private const int MaximumDisplayNameLength = 512;
    private const int MaximumEmailLength = 512;
    private const int MaximumTokenLength = 8_192;
    private const int MaximumUrlLength = 16_384;
    private const int DefaultMaximumPayloadBytes = 1_048_576;
    private static readonly TimeSpan MutationLockTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MutationLockRetryDelay =
        TimeSpan.FromMilliseconds(25);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 20,
    };

    private readonly string filePath;
    private readonly string directoryPath;
    private readonly string lockFilePath;
    private readonly int maximumPayloadBytes;
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private bool disposed;

    public GitHubAccountStore(
        string filePath,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            filePath.Any(character =>
                character <= '\u001F' || character == '\u007F') ||
            !Path.IsPathFullyQualified(filePath))
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.InvalidConfiguration);
        }

        try
        {
            this.filePath = Path.GetFullPath(filePath);
            directoryPath = Path.GetDirectoryName(this.filePath) ?? string.Empty;
            if (Path.GetFileName(this.filePath).Length == 0)
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.InvalidConfiguration);
            }
        }
        catch
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.InvalidConfiguration);
        }

        if (directoryPath.Length == 0 ||
            maximumPayloadBytes < 1 ||
            maximumPayloadBytes > 4 * 1024 * 1024)
        {
            throw new GitHubAccountStoreException(
                maximumPayloadBytes is < 1 or > 4 * 1024 * 1024
                    ? GitHubAccountStoreErrorKind.PayloadTooLarge
                    : GitHubAccountStoreErrorKind.InvalidConfiguration);
        }

        lockFilePath = this.filePath + ".lock";
        this.maximumPayloadBytes = maximumPayloadBytes;
    }

    /// <summary>Lists current summaries without loading tokens into the result.</summary>
    public async Task<IReadOnlyList<GitHubAccountSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            return state.Accounts
                .Select(ToSummary)
                .ToArray();
        }
        finally
        {
            updateGate.Release();
        }
    }

    /// <summary>
    /// Restores one account and its host-bound session by API origin and ID.
    /// </summary>
    public async Task<GitHubStoredAccount?> LoadAsync(
        Uri apiOrigin,
        long accountId,
        CancellationToken cancellationToken = default)
    {
        var normalizedOrigin = NormalizeApiOrigin(apiOrigin);
        ValidateAccountId(accountId);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            var account = state.Accounts.FirstOrDefault(candidate =>
                candidate.Id == accountId &&
                GitHubApiEndpoint.AreSame(
                    ParseStoredApiOrigin(candidate.ApiOrigin),
                    normalizedOrigin));
            return account is null ? null : ToStoredAccount(account);
        }
        finally
        {
            updateGate.Release();
        }
    }

    /// <summary>
    /// Upserts a profile after verifying the profile and session share one
    /// immutable API origin. The token remains only in the protected payload
    /// and returned in-memory session.
    /// </summary>
    public async Task<GitHubStoredAccount> SaveAsync(
        GitHubAuthenticatedProfile profile,
        GitHubAccountSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(session);
        var normalizedOrigin = NormalizeApiOrigin(profile.ApiOrigin);
        if (!GitHubApiEndpoint.AreSame(normalizedOrigin, session.ApiOrigin))
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.InvalidConfiguration);
        }

        ValidateProfile(profile, normalizedOrigin);
        _ = GitHubAccountSession.FromPersistedToken(
            normalizedOrigin,
            session.AccessToken);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquireMutationLockAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var state = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            var persisted = ToPersistedAccount(profile, session, normalizedOrigin);
            var index = state.Accounts.FindIndex(account =>
                account.Id == persisted.Id &&
                GitHubApiEndpoint.AreSame(
                    ParseStoredApiOrigin(account.ApiOrigin),
                    normalizedOrigin));
            if (index >= 0)
            {
                state.Accounts[index] = persisted;
            }
            else
            {
                if (state.Accounts.Count >= MaximumAccounts)
                {
                    throw new GitHubAccountStoreException(
                        GitHubAccountStoreErrorKind.PayloadTooLarge);
                }

                state.Accounts.Add(persisted);
            }

            await WriteCurrentAsync(state, cancellationToken).ConfigureAwait(false);
            return ToStoredAccount(persisted);
        }
        finally
        {
            updateGate.Release();
        }
    }

    /// <summary>Removes one account while preserving every other origin/ID.</summary>
    public async Task<bool> RemoveAsync(
        Uri apiOrigin,
        long accountId,
        CancellationToken cancellationToken = default)
    {
        var normalizedOrigin = NormalizeApiOrigin(apiOrigin);
        ValidateAccountId(accountId);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquireMutationLockAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var state = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            var index = state.Accounts.FindIndex(account =>
                account.Id == accountId &&
                GitHubApiEndpoint.AreSame(
                    ParseStoredApiOrigin(account.ApiOrigin),
                    normalizedOrigin));
            if (index < 0)
            {
                return false;
            }

            state.Accounts.RemoveAt(index);
            await WriteCurrentAsync(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            updateGate.Release();
        }
    }

    /// <summary>Clears only the account file represented by this store.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquireMutationLockAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var state = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (state.Accounts.Count == 0 && !File.Exists(filePath))
            {
                return;
            }

            state.Accounts.Clear();
            await WriteCurrentAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            updateGate.Release();
            throw;
        }
    }

    private async Task<FileStream> AcquireMutationLockAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.StorageUnavailable);
        }
        catch (IOException)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.StorageUnavailable);
        }

        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.WriteThrough);
            }
            catch (UnauthorizedAccessException)
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.StorageUnavailable);
            }
            catch (IOException) when (
                Stopwatch.GetElapsedTime(startedAt) < MutationLockTimeout)
            {
                await Task.Delay(
                        MutationLockRetryDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.StorageUnavailable);
            }
        }
    }

    private async Task<PersistedFile> ReadCurrentAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return NewEmptyFile();
        }

        byte[] encrypted;
        try
        {
            encrypted = await ReadBoundedFileAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GitHubAccountStoreException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return NewEmptyFile();
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.StorageUnavailable);
        }
        catch (IOException)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.StorageUnavailable);
        }

        byte[] decrypted;
        try
        {
            decrypted = UnprotectForCurrentUser(encrypted);
        }
        catch (PlatformNotSupportedException)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.ProtectionUnavailable);
        }
        catch (CryptographicException)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.ProtectionUnavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }

        if (decrypted.Length > maximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(decrypted);
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.PayloadTooLarge);
        }

        try
        {
            var state = JsonSerializer.Deserialize<PersistedFile>(
                decrypted,
                JsonOptions);
            ValidateState(state);
            return state!;
        }
        catch (GitHubAccountStoreException)
        {
            throw;
        }
        catch
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.Corrupt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    private async Task<byte[]> ReadBoundedFileAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 8 * 1024,
            options: FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumPayloadBytes)
        {
            throw new GitHubAccountStoreException(
                stream.Length > maximumPayloadBytes
                    ? GitHubAccountStoreErrorKind.PayloadTooLarge
                    : GitHubAccountStoreErrorKind.Corrupt);
        }

        var length = checked((int)stream.Length);
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken)
            .ConfigureAwait(false);
        if (stream.Length != length)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.Corrupt);
        }

        return buffer;
    }

    private async Task WriteCurrentAsync(
        PersistedFile state,
        CancellationToken cancellationToken)
    {
        ValidateState(state);
        byte[] plaintext;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        }
        catch
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.Corrupt);
        }

        if (plaintext.Length > maximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.PayloadTooLarge);
        }

        byte[] encrypted;
        try
        {
            encrypted = ProtectForCurrentUser(plaintext);
        }
        catch (PlatformNotSupportedException)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.ProtectionUnavailable);
        }
        catch (CryptographicException)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.ProtectionUnavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (encrypted.Length > maximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(encrypted);
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.PayloadTooLarge);
        }

        var temporaryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            try
            {
                Directory.CreateDirectory(directoryPath);
                await using var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 8 * 1024,
                    options: FileOptions.SequentialScan | FileOptions.WriteThrough);
                await stream.WriteAsync(encrypted, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.StorageUnavailable);
            }
            catch (IOException)
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.StorageUnavailable);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(filePath))
                {
                    File.Replace(
                        temporaryPath,
                        filePath,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, filePath);
                }
            }
            catch (UnauthorizedAccessException)
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.StorageUnavailable);
            }
            catch (IOException)
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.StorageUnavailable);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private GitHubStoredAccount ToStoredAccount(PersistedAccount account)
    {
        var apiOrigin = ParseStoredApiOrigin(account.ApiOrigin);
        var profile = new GitHubAuthenticatedProfile(
            apiOrigin,
            account.Id,
            account.Login,
            account.DisplayName,
            account.Email,
            ParseOptionalAvatarUrl(account.AvatarUrl, apiOrigin),
            ParseProfileUrl(account.ProfileUrl, apiOrigin));
        var session = GitHubAccountSession.FromPersistedToken(
            apiOrigin,
            account.AccessToken);
        return new GitHubStoredAccount(profile, session);
    }

    private static PersistedAccount ToPersistedAccount(
        GitHubAuthenticatedProfile profile,
        GitHubAccountSession session,
        Uri apiOrigin) =>
        new()
        {
            ApiOrigin = apiOrigin.AbsoluteUri,
            Id = profile.Id,
            Login = profile.Login,
            DisplayName = profile.DisplayName,
            Email = profile.Email,
            AvatarUrl = profile.AvatarUrl?.AbsoluteUri,
            ProfileUrl = profile.ProfileUrl.AbsoluteUri,
            AccessToken = session.AccessToken,
        };

    private static GitHubAccountSummary ToSummary(PersistedAccount account) =>
        new(ToStoredProfile(account));

    private static GitHubAuthenticatedProfile ToStoredProfile(
        PersistedAccount account)
    {
        var apiOrigin = ParseStoredApiOrigin(account.ApiOrigin);
        return new GitHubAuthenticatedProfile(
            apiOrigin,
            account.Id,
            account.Login,
            account.DisplayName,
            account.Email,
            ParseOptionalAvatarUrl(account.AvatarUrl, apiOrigin),
            ParseProfileUrl(account.ProfileUrl, apiOrigin));
    }

    private static Uri NormalizeApiOrigin(Uri apiOrigin)
    {
        ArgumentNullException.ThrowIfNull(apiOrigin);
        if (GitHubApiEndpoint.IsHost(apiOrigin, "api.github.com"))
        {
            if (apiOrigin.AbsolutePath.TrimEnd('/') != string.Empty)
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.InvalidConfiguration);
            }

            return GitHubApiEndpoint.GitHubCom;
        }

        try
        {
            return GitHubApiEndpoint.ForEnterprise(apiOrigin);
        }
        catch (ArgumentException)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.InvalidConfiguration);
        }
    }

    private static Uri ParseStoredApiOrigin(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumUrlLength ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.Corrupt);
        }

        try
        {
            var normalized = NormalizeApiOrigin(uri);
            if (!GitHubApiEndpoint.AreSame(normalized, uri))
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.Corrupt);
            }

            return normalized;
        }
        catch (GitHubAccountStoreException)
        {
            throw;
        }
        catch
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.Corrupt);
        }
    }

    private static void ValidateProfile(
        GitHubAuthenticatedProfile profile,
        Uri normalizedOrigin)
    {
        var displayName = profile.DisplayName;
        var email = profile.Email;
        if (profile.Id <= 0 ||
            profile.Login.Length == 0 ||
            profile.Login.Length > MaximumLoginLength ||
            ContainsControlCharacter(profile.Login) ||
            (displayName is not null && displayName.Length > MaximumDisplayNameLength) ||
            (displayName is not null && ContainsControlCharacter(displayName)) ||
            (email is not null && email.Length > MaximumEmailLength) ||
            (email is not null && ContainsControlCharacter(email)) ||
            profile.ProfileUrl.AbsoluteUri.Length > MaximumUrlLength ||
            !IsAllowedProfileUrl(profile.ProfileUrl, normalizedOrigin))
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.InvalidConfiguration);
        }

        if (profile.AvatarUrl is not null &&
            (profile.AvatarUrl.AbsoluteUri.Length > MaximumUrlLength ||
             !IsAllowedAvatarUrl(profile.AvatarUrl, normalizedOrigin)))
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.InvalidConfiguration);
        }

    }

    private static Uri ParseProfileUrl(string value, Uri apiOrigin)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumUrlLength ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !IsAllowedProfileUrl(uri, apiOrigin))
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.Corrupt);
        }

        return uri;
    }

    private static Uri? ParseOptionalAvatarUrl(string? value, Uri apiOrigin)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumUrlLength ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!IsAllowedAvatarUrl(uri, apiOrigin))
        {
            return null;
        }

        return uri;
    }

    private static bool IsAllowedProfileUrl(Uri value, Uri apiOrigin)
    {
        if (!IsSafeHttpsUri(value))
        {
            return false;
        }

        return SameOrigin(value, GitHubApiEndpoint.WebOriginForApi(apiOrigin));
    }

    private static bool IsAllowedAvatarUrl(Uri value, Uri apiOrigin)
    {
        if (!IsSafeHttpsUri(value))
        {
            return false;
        }

        if (SameOrigin(value, GitHubApiEndpoint.WebOriginForApi(apiOrigin)))
        {
            return true;
        }

        return (GitHubApiEndpoint.IsHost(apiOrigin, "api.github.com") ||
                GitHubApiEndpoint.IsGheCloudHost(apiOrigin)) &&
            value.Host.EndsWith(
                ".githubusercontent.com",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeHttpsUri(Uri value) =>
        value.IsAbsoluteUri &&
        string.Equals(
            value.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase) &&
        value.UserInfo.Length == 0 &&
        string.IsNullOrEmpty(value.Fragment);

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static void ValidateState(PersistedFile? state)
    {
        if (state is null ||
            !string.Equals(state.Format, FileFormat, StringComparison.Ordinal) ||
            state.Version != FileVersion ||
            state.Accounts is null ||
            state.Accounts.Count > MaximumAccounts)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.Corrupt);
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in state.Accounts)
        {
            ValidatePersistedAccount(account);
            var origin = ParseStoredApiOrigin(account.ApiOrigin);
            var key = $"{origin.AbsoluteUri}|{account.Id}";
            if (!keys.Add(key))
            {
                throw new GitHubAccountStoreException(
                    GitHubAccountStoreErrorKind.Corrupt);
            }
        }
    }

    private static void ValidatePersistedAccount(PersistedAccount? account)
    {
        var displayName = account?.DisplayName;
        var email = account?.Email;
        if (account is null ||
            account.Id <= 0 ||
            string.IsNullOrWhiteSpace(account.Login) ||
            account.Login.Length > MaximumLoginLength ||
            ContainsControlCharacter(account.Login) ||
            (displayName is not null && displayName.Length > MaximumDisplayNameLength) ||
            (displayName is not null && ContainsControlCharacter(displayName)) ||
            (email is not null && email.Length > MaximumEmailLength) ||
            (email is not null && ContainsControlCharacter(email)) ||
            string.IsNullOrWhiteSpace(account.AccessToken) ||
            account.AccessToken.Length > MaximumTokenLength ||
            ContainsControlCharacter(account.AccessToken))
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.Corrupt);
        }

        var apiOrigin = ParseStoredApiOrigin(account.ApiOrigin);
        _ = ParseProfileUrl(account.ProfileUrl, apiOrigin);
        if (account.AvatarUrl is not null &&
            (account.AvatarUrl.Length > MaximumUrlLength ||
             !Uri.TryCreate(account.AvatarUrl, UriKind.Absolute, out var avatar) ||
             !IsAllowedAvatarUrl(avatar, apiOrigin)))
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.Corrupt);
        }
    }

    private static PersistedFile NewEmptyFile() =>
        new()
        {
            Format = FileFormat,
            Version = FileVersion,
            Accounts = [],
        };

    private static void ValidateAccountId(long accountId)
    {
        if (accountId <= 0)
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.InvalidConfiguration);
        }
    }

    private static bool ContainsControlCharacter(string value) =>
        value.Any(character =>
            character <= '\u001F' || character == '\u007F');

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The final replacement is already complete or the original file
            // remains untouched. A best-effort cleanup avoids hiding that result.
        }
    }

    private static byte[] ProtectForCurrentUser(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.ProtectionUnavailable);
        }

        return ProtectedData.Protect(
            plaintext,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
    }

    private static byte[] UnprotectForCurrentUser(byte[] encrypted)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new GitHubAccountStoreException(
                GitHubAccountStoreErrorKind.ProtectionUnavailable);
        }

        return ProtectedData.Unprotect(
            encrypted,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        updateGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(GitHubAccountStore));
        }
    }

    private sealed class PersistedFile
    {
        public string Format { get; set; } = FileFormat;

        public int Version { get; set; } = FileVersion;

        public List<PersistedAccount> Accounts { get; set; } = [];
    }

    private sealed class PersistedAccount
    {
        public string ApiOrigin { get; set; } = string.Empty;

        public long Id { get; set; }

        public string Login { get; set; } = string.Empty;

        public string? DisplayName { get; set; }

        public string? Email { get; set; }

        public string? AvatarUrl { get; set; }

        public string ProfileUrl { get; set; } = string.Empty;

        public string AccessToken { get; set; } = string.Empty;
    }
}
