using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinGit.Native;

internal sealed class NativeSettings
{
    public const int MaximumRepositoryAliasLength = 100;
    public const string DefaultImageDiffMode = "TwoUp";
    public const string DefaultTextDiffMode = "Unified";

    public string Theme { get; set; } = "System";

    public string ImageDiffMode { get; set; } = DefaultImageDiffMode;

    public string TextDiffMode { get; set; } = DefaultTextDiffMode;

    public bool HideWhitespaceChanges { get; set; }

    public string? EditorId { get; set; }

    public string? ShellId { get; set; }

    public List<string> RecentRepositories { get; set; } = [];

    public Dictionary<string, string> RepositoryAliases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The shared Codex model choice. A null value follows the catalog default.
    /// </summary>
    public string? CodexModelId { get; set; }

    /// <summary>
    /// The shared Codex reasoning choice. A null value follows the model default.
    /// </summary>
    public string? CodexReasoningEffort { get; set; }

    /// <summary>
    /// Path-free repository keys and the time each commit-message data-sharing
    /// notice was accepted. The native app never stores repository paths here.
    /// </summary>
    public Dictionary<string, DateTimeOffset> CodexCommitMessageConsentTimestamps { get; set; } = [];

    /// <summary>
    /// Path-free repository keys and the time each selected-changes review
    /// data-sharing notice was accepted. This receipt is intentionally
    /// separate from the staged-only commit-message receipt because a review
    /// can include unstaged content.
    /// </summary>
    public Dictionary<string, DateTimeOffset> CodexSelectedChangesReviewConsentTimestamps { get; set; } = [];
}

/// <summary>
/// Keeps native-only preferences in LocalAppData. Electron's IndexedDB and
/// localStorage profiles intentionally stay outside this store.
/// </summary>
internal static class NativeSettingsStore
{
    private const string SettingsDirectoryEnvironmentVariable =
        "WINGIT_NATIVE_SETTINGS_DIRECTORY";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    private static string SettingsDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(
                SettingsDirectoryEnvironmentVariable,
                EnvironmentVariableTarget.Process);
            if (overridePath is null)
            {
                var localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    throw new InvalidOperationException(
                        "The native LocalAppData path is unavailable.");
                }

                return Path.Combine(
                    localAppData,
                    "WinGit.Native");
            }

            if (string.IsNullOrWhiteSpace(overridePath) || !Path.IsPathFullyQualified(overridePath))
            {
                throw new InvalidOperationException(
                    $"{SettingsDirectoryEnvironmentVariable} must contain a fully qualified path.");
            }

            return overridePath;
        }
    }

    internal static string ProfileDirectory => SettingsDirectory;

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static async Task<NativeSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new NativeSettings();
            }

            await using var stream = File.OpenRead(SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<NativeSettings>(stream, JsonOptions)
                ?? new NativeSettings();
            Normalize(settings);
            return settings;
        }
        catch (UnauthorizedAccessException)
        {
            return new NativeSettings();
        }
        catch (IOException)
        {
            return new NativeSettings();
        }
        catch (JsonException)
        {
            return new NativeSettings();
        }
    }

    public static async Task SaveAsync(NativeSettings settings)
    {
        await SaveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var snapshot = new NativeSettings
            {
                Theme = settings.Theme,
                ImageDiffMode = settings.ImageDiffMode,
                TextDiffMode = settings.TextDiffMode,
                HideWhitespaceChanges = settings.HideWhitespaceChanges,
                EditorId = settings.EditorId,
                ShellId = settings.ShellId,
                RecentRepositories = settings.RecentRepositories is null
                    ? []
                    : [.. settings.RecentRepositories],
                RepositoryAliases = settings.RepositoryAliases is null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new(settings.RepositoryAliases, StringComparer.OrdinalIgnoreCase),
                CodexModelId = settings.CodexModelId,
                CodexReasoningEffort = settings.CodexReasoningEffort,
                CodexCommitMessageConsentTimestamps = settings.CodexCommitMessageConsentTimestamps is null
                    ? []
                    : new(settings.CodexCommitMessageConsentTimestamps),
                CodexSelectedChangesReviewConsentTimestamps = settings.CodexSelectedChangesReviewConsentTimestamps is null
                    ? []
                    : new(settings.CodexSelectedChangesReviewConsentTimestamps),
            };
            Normalize(snapshot);
            var temporaryPath = SettingsPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

            try
            {
                await using (var stream = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions);
                }

                File.Move(temporaryPath, SettingsPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch (IOException)
                {
                    // The successful atomic move already left the durable value.
                }
                catch (UnauthorizedAccessException)
                {
                    // The caller receives the original write failure, if any.
                }
            }
        }
        finally
        {
            SaveGate.Release();
        }
    }

    public static void AddRecentRepository(NativeSettings settings, string rootPath)
    {
        Normalize(settings);
        var normalized = NormalizeRepositoryPath(rootPath);
        settings.RecentRepositories.RemoveAll(path =>
            string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase));
        settings.RecentRepositories.Insert(0, normalized);
    }

    public static string? GetRepositoryAlias(NativeSettings settings, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = NormalizeRepositoryPath(rootPath);
        if (settings.RepositoryAliases is null)
        {
            return null;
        }

        if (settings.RepositoryAliases.TryGetValue(normalized, out var alias))
        {
            return alias;
        }

        return settings.RepositoryAliases
            .FirstOrDefault(entry => string.Equals(
                entry.Key,
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    public static void SetRepositoryAlias(
        NativeSettings settings,
        string rootPath,
        string? alias)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = NormalizeRepositoryPath(rootPath);
        var normalizedAlias = NormalizeRepositoryAlias(alias);
        settings.RepositoryAliases ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var key in settings.RepositoryAliases.Keys
                     .Where(key => string.Equals(
                         key,
                         normalized,
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            settings.RepositoryAliases.Remove(key);
        }

        if (normalizedAlias is not null)
        {
            settings.RepositoryAliases[normalized] = normalizedAlias;
        }
    }

    public static bool RemoveRecentRepository(NativeSettings settings, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = NormalizeRepositoryPath(rootPath);
        var removed = settings.RecentRepositories.RemoveAll(path =>
            string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase));
        SetRepositoryAlias(settings, normalized, null);
        return removed > 0;
    }

    public static string NormalizeRepositoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A repository path is required.", nameof(path));
        }

        var normalized = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(normalized);
        return !string.IsNullOrWhiteSpace(pathRoot)
            && string.Equals(normalized, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? pathRoot
            : normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool HasSelectedChangesReviewConsent(NativeSettings settings, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var key = GetRepositoryConsentKey(rootPath);
        var now = DateTimeOffset.UtcNow;
        return settings.CodexSelectedChangesReviewConsentTimestamps is not null
            && settings.CodexSelectedChangesReviewConsentTimestamps.TryGetValue(key, out var acceptedAt)
            && acceptedAt >= now - TimeSpan.FromDays(30)
            && acceptedAt <= now.AddMinutes(5);
    }

    public static void AcknowledgeSelectedChangesReviewConsent(
        NativeSettings settings,
        string rootPath)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.CodexSelectedChangesReviewConsentTimestamps ??= [];
        settings.CodexSelectedChangesReviewConsentTimestamps[GetRepositoryConsentKey(rootPath)] =
            DateTimeOffset.UtcNow;
    }

    private static string GetRepositoryConsentKey(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A repository path is required.", nameof(rootPath));
        }

        var normalized = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static void Normalize(NativeSettings settings)
    {
        settings.Theme = settings.Theme is "System" or "Light" or "Dark" ? settings.Theme : "System";
        settings.ImageDiffMode = NormalizeImageDiffMode(settings.ImageDiffMode);
        settings.TextDiffMode = NormalizeTextDiffMode(settings.TextDiffMode);
        settings.EditorId = NormalizeSelectionValue(settings.EditorId);
        settings.ShellId = NormalizeSelectionValue(settings.ShellId);
        settings.RecentRepositories ??= [];
        settings.RecentRepositories = settings.RecentRepositories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(TryNormalizeRepositoryPath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.RepositoryAliases ??= new(StringComparer.OrdinalIgnoreCase);
        var recentRepositoryPaths = settings.RecentRepositories.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        settings.RepositoryAliases = settings.RepositoryAliases
            .Select(entry =>
            {
                try
                {
                    var normalizedPath = NormalizeRepositoryPath(entry.Key);
                    var normalizedAlias = NormalizeRepositoryAlias(entry.Value);
                    return (Path: normalizedPath, Alias: normalizedAlias);
                }
                catch (Exception)
                {
                    return (Path: string.Empty, Alias: null);
                }
            })
            .Where(entry => entry.Alias is not null && recentRepositoryPaths.Contains(entry.Path))
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Alias!,
                StringComparer.OrdinalIgnoreCase);
        settings.CodexModelId = NormalizeSelectionValue(settings.CodexModelId);
        settings.CodexReasoningEffort = NormalizeSelectionValue(settings.CodexReasoningEffort);
        settings.CodexCommitMessageConsentTimestamps ??= [];
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - TimeSpan.FromDays(30);
        settings.CodexCommitMessageConsentTimestamps = settings.CodexCommitMessageConsentTimestamps
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Key) &&
                entry.Key.Length <= 128 &&
                entry.Value >= cutoff &&
                entry.Value <= now.AddMinutes(5))
            .Take(10_000)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        settings.CodexSelectedChangesReviewConsentTimestamps ??= [];
        settings.CodexSelectedChangesReviewConsentTimestamps = settings.CodexSelectedChangesReviewConsentTimestamps
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Key) &&
                entry.Key.Length <= 128 &&
                entry.Value >= cutoff &&
                entry.Value <= now.AddMinutes(5))
            .Take(10_000)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    private static string? TryNormalizeRepositoryPath(string path)
    {
        try
        {
            return NormalizeRepositoryPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? NormalizeSelectionValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 200 ? null : value.Trim();

    internal static string NormalizeImageDiffMode(string? value) =>
        value switch
        {
            "TwoUp" or "Swipe" or "OnionSkin" or "Difference" => value,
            _ => NativeSettings.DefaultImageDiffMode,
        };

    internal static string NormalizeTextDiffMode(string? value) =>
        value is "Unified" or "Split"
            ? value
            : NativeSettings.DefaultTextDiffMode;

    private static string? NormalizeRepositoryAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        var normalized = alias.Trim();
        return normalized.Length > NativeSettings.MaximumRepositoryAliasLength
            ? null
            : normalized;
    }
}
