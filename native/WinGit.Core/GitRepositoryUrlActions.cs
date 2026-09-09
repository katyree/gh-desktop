using System.Globalization;
using System.Text.RegularExpressions;

namespace WinGit.Core;

/// <summary>The deliberate native action represented by a repository URL.</summary>
public enum RepositoryUrlActionKind
{
    OpenRepository,
}

/// <summary>
/// A parsed <c>wingit://openRepo/...</c> action. The values are data only;
/// opening, cloning, fetching, and opening a file are host responsibilities.
/// </summary>
public sealed record RepositoryUrlAction(
    RepositoryUrlActionKind Kind,
    string RemoteUrl,
    string? Branch,
    int? PullRequestNumber,
    string? FilePath);

/// <summary>
/// Parses the repository-opening protocol shape used by the Electron app.
/// This parser performs no Git, network, authentication, or process work.
/// </summary>
public static class RepositoryUrlParser
{
    private const string OpenRepositoryScheme = "wingit";
    private const string OpenRepositoryHost = "openrepo";
    private const int MaximumInputLength = 16_384;
    private const int MaximumRemoteLength = 16_384;

    // Keep this in step with app/src/lib/sanitize-ref-name.ts. The native
    // parser is stricter only in that it does not carry JavaScript regex state
    // across calls.
    private static readonly Regex InvalidBranchPattern = new(
        @"[\x00-\x20\x7F~^:?*\[\\|""<>]|@\{|\.\.+|^\.|\.$|\.lock$|/$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EmbeddedHttpCredentialPattern = new(
        @"(?i)^https?://[^\s/?#@]+@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EmbeddedSchemeCredentialPattern = new(
        @"(?i)^[A-Za-z][A-Za-z0-9+.-]*://[^\s/?#@]+:[^\s/?#@]*@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EmbeddedScpCredentialPattern = new(
        @"(?i)^[^\s/:@]+:[^\s/@]+@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeHostPattern = new(
        @"^(?:[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Parses one registered WinGit repository URL.
    /// </summary>
    public static bool TryParse(
        string? value,
        out RepositoryUrlAction? action)
    {
        action = null;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumInputLength ||
            ContainsMalformedEscape(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, OpenRepositoryScheme, StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length > 0 ||
            uri.Port >= 0)
        {
            return false;
        }

        if (!TryGetRawParts(value, out var rawPath, out var rawQuery) ||
            rawPath.Length <= 1 ||
            !TryDecodeQuery(rawQuery, out var query))
        {
            return false;
        }

        var remoteUrl = rawPath[1..];
        if (!IsSupportedRemote(remoteUrl))
        {
            return false;
        }

        var branch = GetFirstQueryValue(query, "branch");
        var pullRequestText = GetFirstQueryValue(query, "pr");
        var filePath = GetFirstQueryValue(query, "filepath");
        if (branch is not null && InvalidBranchPattern.IsMatch(branch))
        {
            return false;
        }

        int? pullRequestNumber = null;
        if (pullRequestText is not null)
        {
            if (!int.TryParse(
                    pullRequestText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedPullRequest) ||
                parsedPullRequest <= 0)
            {
                return false;
            }

            if (branch is not null &&
                !Regex.IsMatch(
                    branch,
                    @"^pr/\d+$",
                    RegexOptions.CultureInvariant))
            {
                return false;
            }

            pullRequestNumber = parsedPullRequest;
        }

        action = new RepositoryUrlAction(
            RepositoryUrlActionKind.OpenRepository,
            remoteUrl,
            branch,
            pullRequestNumber,
            filePath);
        return true;
    }

    private static bool TryGetRawParts(
        string value,
        out string rawPath,
        out string rawQuery)
    {
        rawPath = string.Empty;
        rawQuery = string.Empty;

        var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator <= 0)
        {
            return false;
        }

        var authorityStart = schemeSeparator + 3;
        var authorityEnd = FindFirst(value, authorityStart, '/', '?', '#');
        if (authorityEnd <= authorityStart ||
            authorityEnd >= value.Length ||
            !string.Equals(
                value[authorityStart..authorityEnd],
                OpenRepositoryHost,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value[authorityEnd] != '/')
        {
            return false;
        }

        var queryStart = value.IndexOf('?', authorityEnd);
        var fragmentStart = value.IndexOf('#', authorityEnd);
        var pathEnd = value.Length;
        if (queryStart >= 0 && (fragmentStart < 0 || queryStart < fragmentStart))
        {
            pathEnd = queryStart;
        }
        else if (fragmentStart >= 0)
        {
            pathEnd = fragmentStart;
        }

        rawPath = value[authorityEnd..pathEnd];
        if (queryStart >= 0 && (fragmentStart < 0 || queryStart < fragmentStart))
        {
            var queryEnd = fragmentStart >= 0 ? fragmentStart : value.Length;
            rawQuery = value[(queryStart + 1)..queryEnd];
        }

        return true;
    }

    private static bool TryDecodeQuery(
        string rawQuery,
        out IReadOnlyDictionary<string, string> query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in rawQuery.Split('&', StringSplitOptions.None))
        {
            if (pair.Length == 0)
            {
                continue;
            }

            var separator = pair.IndexOf('=');
            var rawKey = separator < 0 ? pair : pair[..separator];
            var rawValue = separator < 0 ? string.Empty : pair[(separator + 1)..];
            if (!TryDecodeQueryComponent(rawKey, out var key) ||
                !TryDecodeQueryComponent(rawValue, out var value))
            {
                query = values;
                return false;
            }

            // Node's querystring parser exposes duplicate keys as an array;
            // the Electron helper deliberately uses its first value.
            values.TryAdd(key, value);
        }

        query = values;
        return true;
    }

    private static bool TryDecodeQueryComponent(
        string rawValue,
        out string value)
    {
        try
        {
            value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            value = string.Empty;
            return false;
        }

        return !ContainsQueryControl(value);
    }

    private static string? GetFirstQueryValue(
        IReadOnlyDictionary<string, string> query,
        string key) =>
        query.TryGetValue(key, out var value) ? value : null;

    private static bool IsSupportedRemote(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) ||
            remoteUrl.Length > MaximumRemoteLength ||
            remoteUrl[0] == '-' ||
            ContainsControl(remoteUrl) ||
            ContainsEncodedControl(remoteUrl) ||
            EmbeddedHttpCredentialPattern.IsMatch(remoteUrl) ||
            EmbeddedSchemeCredentialPattern.IsMatch(remoteUrl) ||
            EmbeddedScpCredentialPattern.IsMatch(remoteUrl))
        {
            return false;
        }

        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return uri.UserInfo.Length == 0 &&
                    uri.Host.Length > 0 &&
                    uri.AbsolutePath.Length > 1 &&
                    string.IsNullOrEmpty(uri.Query) &&
                    string.IsNullOrEmpty(uri.Fragment);
            }

            if (string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                return uri.Host.Length > 0 &&
                    uri.UserInfo.IndexOf(':') < 0 &&
                    uri.AbsolutePath.Length > 1 &&
                    string.IsNullOrEmpty(uri.Query) &&
                    string.IsNullOrEmpty(uri.Fragment);
            }

            if (string.Equals(uri.Scheme, "git", StringComparison.OrdinalIgnoreCase))
            {
                return IsSafeGitAuthorityPath(remoteUrl[4..]);
            }

            return false;
        }

        // GitHub Desktop's existing URL tests also exercise the slash-shaped
        // SSH payload (git@host/owner/repository), alongside scp syntax.
        var at = remoteUrl.IndexOf('@');
        if (at <= 0 || at == remoteUrl.Length - 1)
        {
            return false;
        }

        var separator = remoteUrl.IndexOf(':', at + 1);
        var slash = remoteUrl.IndexOf('/', at + 1);
        if (separator < 0 || (slash >= 0 && slash < separator))
        {
            separator = slash;
        }

        return separator > at + 1 &&
            separator < remoteUrl.Length - 1 &&
            IsSafeHost(remoteUrl[(at + 1)..separator]) &&
            !remoteUrl[(separator + 1)..].StartsWith("/", StringComparison.Ordinal);
    }

    private static bool IsSafeGitAuthorityPath(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        var separator = value.IndexOf('/');
        return separator > 0 &&
            separator < value.Length - 1 &&
            IsSafeHost(value[..separator]) &&
            !value[(separator + 1)..].StartsWith("/", StringComparison.Ordinal);
    }

    private static bool IsSafeHost(string host) => SafeHostPattern.IsMatch(host);

    private static bool ContainsMalformedEscape(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length ||
                !IsHex(value[index + 1]) ||
                !IsHex(value[index + 2]))
            {
                return true;
            }

            index += 2;
        }

        return false;
    }

    private static bool ContainsEncodedControl(string value)
    {
        for (var index = 0; index + 2 < value.Length; index++)
        {
            if (value[index] != '%' ||
                !IsHex(value[index + 1]) ||
                !IsHex(value[index + 2]))
            {
                continue;
            }

            var decoded = (HexValue(value[index + 1]) << 4) | HexValue(value[index + 2]);
            if (decoded <= 0x20 || decoded == 0x7F)
            {
                return true;
            }

            index += 2;
        }

        return false;
    }

    private static bool ContainsControl(string value) =>
        value.Any(character => character <= 0x20 || character == 0x7F);

    private static bool ContainsQueryControl(string value) =>
        value.Any(character => character < 0x20 || character == 0x7F);

    private static int FindFirst(string value, int start, params char[] characters)
    {
        var result = value.Length;
        foreach (var character in characters)
        {
            var index = value.IndexOf(character, start);
            if (index >= 0 && index < result)
            {
                result = index;
            }
        }

        return result;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int HexValue(char value) =>
        value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => value - 'A' + 10,
        };
}
