using System.Globalization;
using System.Net;
using System.Text.Json;

namespace WinGit.Core.GitHub;

/// <summary>Options for one explicit, read-only branch-rules request.</summary>
public sealed class GitHubRepositoryRulesClientOptions
{
    public const int DefaultMaximumResponseBytes = 256 * 1024;
    public const int DefaultMaximumRulesetReads = 100;
    public const int DefaultMaximumBranchPages = 100;

    private GitHubRepositoryRulesClientOptions(
        Uri apiOrigin,
        TimeSpan requestTimeout,
        int maximumResponseBytes,
        int maximumRulesetReads,
        int maximumBranchPages)
    {
        ApiOrigin = apiOrigin;
        RequestTimeout = requestTimeout;
        MaximumResponseBytes = maximumResponseBytes;
        MaximumRulesetReads = maximumRulesetReads;
        MaximumBranchPages = maximumBranchPages;
    }

    /// <summary>Uses https://api.github.com/repos/{owner}/{repo}/rules/...</summary>
    public static GitHubRepositoryRulesClientOptions ForGitHubCom(
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes,
        int maximumRulesetReads = DefaultMaximumRulesetReads,
        int maximumBranchPages = DefaultMaximumBranchPages) =>
        Create(
            GitHubApiEndpoint.GitHubCom,
            requestTimeout,
            maximumResponseBytes,
            maximumRulesetReads,
            maximumBranchPages);

    /// <summary>Uses an Enterprise host's /api/v3 rules endpoint.</summary>
    public static GitHubRepositoryRulesClientOptions ForEnterprise(
        Uri enterpriseHost,
        TimeSpan? requestTimeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes,
        int maximumRulesetReads = DefaultMaximumRulesetReads,
        int maximumBranchPages = DefaultMaximumBranchPages) =>
        Create(
            GitHubApiEndpoint.ForEnterprise(enterpriseHost),
            requestTimeout,
            maximumResponseBytes,
            maximumRulesetReads,
            maximumBranchPages);

    public Uri ApiOrigin { get; }

    public TimeSpan RequestTimeout { get; }

    public int MaximumResponseBytes { get; }

    public int MaximumRulesetReads { get; }

    public int MaximumBranchPages { get; }

    private static GitHubRepositoryRulesClientOptions Create(
        Uri apiOrigin,
        TimeSpan? requestTimeout,
        int maximumResponseBytes,
        int maximumRulesetReads,
        int maximumBranchPages)
    {
        var timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The GitHub request timeout must be between one tick and ten minutes.");
        }

        if (maximumResponseBytes < 1 || maximumResponseBytes > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResponseBytes),
                "The GitHub response limit must be between 1 and 1048576 bytes.");
        }

        if (maximumRulesetReads < 1 || maximumRulesetReads > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRulesetReads),
                "The ruleset request limit must be between 1 and 1000 reads.");
        }

        if (maximumBranchPages < 1 || maximumBranchPages > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBranchPages),
                "The branch page limit must be between 1 and 1000 pages.");
        }

        return new GitHubRepositoryRulesClientOptions(
            apiOrigin,
            timeout,
            maximumResponseBytes,
            maximumRulesetReads,
            maximumBranchPages);
    }
}

public enum GitHubRepositoryRulesAvailability
{
    Known,
    Partial,
    Unavailable,
}

public enum GitHubRepositoryRulesErrorKind
{
    InvalidConfiguration,
    SessionMismatch,
    Network,
    Timeout,
    Unauthorized,
    Forbidden,
    RateLimited,
    SamlRequired,
    NotFound,
    InvalidResponse,
    PageLimitReached,
    RulesetLimitReached,
}

/// <summary>Sanitized branch-rules failure.</summary>
public sealed class GitHubRepositoryRulesException : InvalidOperationException
{
    public GitHubRepositoryRulesException(GitHubRepositoryRulesErrorKind kind)
        : base(GetMessage(kind))
    {
        Kind = kind;
    }

    public GitHubRepositoryRulesErrorKind Kind { get; }

    private static string GetMessage(GitHubRepositoryRulesErrorKind kind) =>
        kind switch
        {
            GitHubRepositoryRulesErrorKind.InvalidConfiguration =>
                "GitHub branch rules are not configured.",
            GitHubRepositoryRulesErrorKind.SessionMismatch =>
                "The GitHub account session belongs to a different host.",
            GitHubRepositoryRulesErrorKind.Network =>
                "GitHub branch rules could not be reached.",
            GitHubRepositoryRulesErrorKind.Timeout =>
                "The GitHub branch-rules request timed out.",
            GitHubRepositoryRulesErrorKind.Unauthorized =>
                "GitHub rejected the branch-rules credentials.",
            GitHubRepositoryRulesErrorKind.Forbidden =>
                "GitHub refused access to branch rules.",
            GitHubRepositoryRulesErrorKind.RateLimited =>
                "GitHub rate limited the branch-rules request.",
            GitHubRepositoryRulesErrorKind.SamlRequired =>
                "GitHub requires organization sign-in before branch-rule access.",
            GitHubRepositoryRulesErrorKind.NotFound =>
                "GitHub could not find the branch rules.",
            GitHubRepositoryRulesErrorKind.InvalidResponse =>
                "GitHub returned an invalid branch-rules response.",
            GitHubRepositoryRulesErrorKind.PageLimitReached =>
                "The GitHub branch-rules page limit was reached.",
            GitHubRepositoryRulesErrorKind.RulesetLimitReached =>
                "The GitHub ruleset request limit was reached.",
            _ => "GitHub branch-rule access failed.",
        };
}

public sealed class GitHubRepositoryRulesCancelledException
    : OperationCanceledException
{
    public GitHubRepositoryRulesCancelledException()
        : base("GitHub branch-rule loading was cancelled.")
    {
    }
}

public enum GitHubCommitMessageRuleOperator
{
    StartsWith,
    EndsWith,
    Contains,
    Regex,
}

public enum GitHubCommitMessageRuleBypassMode
{
    Always,
    PullRequestsOnly,
    Never,
}

/// <summary>
/// One validated commit-message rule and its ruleset bypass metadata. The
/// description is generated from the typed fields and contains no raw API
/// diagnostic text.
/// </summary>
public sealed class GitHubCommitMessageRule
{
    internal GitHubCommitMessageRule(
        long rulesetId,
        GitHubCommitMessageRuleOperator operatorKind,
        bool negate,
        string pattern,
        GitHubCommitMessageRuleBypassMode bypassMode)
    {
        RulesetId = rulesetId;
        Operator = operatorKind;
        Negate = negate;
        Pattern = pattern;
        BypassMode = bypassMode;
        Description = BuildDescription(operatorKind, negate, pattern);
    }

    public long RulesetId { get; }

    public GitHubCommitMessageRuleOperator Operator { get; }

    public bool Negate { get; }

    public string Pattern { get; }

    public GitHubCommitMessageRuleBypassMode BypassMode { get; }

    /// <summary>True only when the ruleset allows an always-bypass action.</summary>
    public bool CanBypass => BypassMode == GitHubCommitMessageRuleBypassMode.Always;

    public string Description { get; }

    public override string ToString() => "GitHub commit-message rule.";

    private static string BuildDescription(
        GitHubCommitMessageRuleOperator operatorKind,
        bool negate,
        string pattern)
    {
        var prefix = negate ? "must not " : "must ";
        return operatorKind switch
        {
            GitHubCommitMessageRuleOperator.Regex =>
                prefix + $"match the regular expression \"{pattern}\"",
            GitHubCommitMessageRuleOperator.StartsWith =>
                prefix + $"start with \"{pattern}\"",
            GitHubCommitMessageRuleOperator.EndsWith =>
                prefix + $"end with \"{pattern}\"",
            GitHubCommitMessageRuleOperator.Contains =>
                prefix + $"contain \"{pattern}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(operatorKind)),
        };
    }
}

/// <summary>
/// Immutable result bound to the API origin, repository, and branch requested
/// by the caller. Known empty means the branch response succeeded and had no
/// commit-message rules; partial means some applicable rule metadata could not
/// be loaded.
/// </summary>
public sealed class GitHubRepositoryRulesResult
{
    internal GitHubRepositoryRulesResult(
        Uri apiOrigin,
        string owner,
        string repositoryName,
        string branch,
        IReadOnlyList<GitHubCommitMessageRule> rules,
        GitHubRepositoryRulesAvailability availability,
        GitHubRepositoryRulesErrorKind? errorKind)
    {
        ApiOrigin = apiOrigin;
        Owner = owner;
        RepositoryName = repositoryName;
        Branch = branch;
        Rules = rules;
        Availability = availability;
        ErrorKind = errorKind;
    }

    public Uri ApiOrigin { get; }

    public string Owner { get; }

    public string RepositoryName { get; }

    public string Branch { get; }

    public IReadOnlyList<GitHubCommitMessageRule> Rules { get; }

    public GitHubRepositoryRulesAvailability Availability { get; }

    public GitHubRepositoryRulesErrorKind? ErrorKind { get; }

    public override string ToString() => "GitHub repository rules result.";
}

/// <summary>
/// Fetches active rules for one branch and resolves only commit-message
/// patterns. It never writes rules, evaluates regexes, or follows server URLs.
/// </summary>
public sealed class GitHubRepositoryRulesClient : IDisposable
{
    private const int PageSize = 100;
    private const int MaximumOwnerOrRepositoryLength = 256;
    private const int MaximumBranchLength = 1_024;
    private const int MaximumPatternLength = 8_192;
    private const int MaximumRuleCount = 100;

    private readonly GitHubRepositoryRulesClientOptions options;
    private readonly GitHubApiTransport transport;
    private bool disposed;

    public GitHubRepositoryRulesClient(
        GitHubRepositoryRulesClientOptions options)
        : this(options, CreateTransport(options))
    {
    }

    internal GitHubRepositoryRulesClient(
        GitHubRepositoryRulesClientOptions options,
        HttpClient httpClient)
        : this(options, CreateTransport(options, httpClient))
    {
    }

    private GitHubRepositoryRulesClient(
        GitHubRepositoryRulesClientOptions options,
        GitHubApiTransport transport)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<GitHubRepositoryRulesResult> GetForBranchAsync(
        GitHubAccountSession session,
        string owner,
        string repositoryName,
        string branch,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        owner = ValidateRepositoryPart(owner, nameof(owner));
        repositoryName = ValidateRepositoryPart(
            repositoryName,
            nameof(repositoryName));
        branch = ValidateBranch(branch);
        if (!GitHubApiEndpoint.AreSame(session.ApiOrigin, options.ApiOrigin))
        {
            throw new GitHubRepositoryRulesException(
                GitHubRepositoryRulesErrorKind.SessionMismatch);
        }

        ThrowIfCancellationRequested(cancellationToken);
        var branchRules = new List<BranchRule>();
        GitHubRepositoryRulesErrorKind? errorKind = null;
        var branchPagesFetched = 0;
        var lastBranchPageWasFull = false;
        for (var pageNumber = 1;
             pageNumber <= options.MaximumBranchPages;
             pageNumber++)
        {
            ThrowIfCancellationRequested(cancellationToken);
            var branchEndpoint = BuildBranchEndpoint(
                options.ApiOrigin,
                owner,
                repositoryName,
                branch,
                pageNumber);
            GitHubApiTransportResponse branchResponse;
            try
            {
                branchResponse = await transport.GetAsync(
                        branchEndpoint,
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GitHubApiTransportException exception)
            {
                if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
                {
                    throw new GitHubRepositoryRulesCancelledException();
                }

                errorKind = MapTransportError(exception.Kind);
                break;
            }

            ThrowIfCancellationRequested(cancellationToken);
            if ((int)branchResponse.StatusCode is < 200 or >= 300)
            {
                errorKind = MapResponseError(branchResponse);
                break;
            }

            BranchPageParseResult parsed;
            try
            {
                parsed = ParseBranchRules(branchResponse.Content);
            }
            catch (GitHubRepositoryRulesException exception)
            {
                errorKind = exception.Kind;
                break;
            }

            branchPagesFetched++;
            branchRules.AddRange(parsed.Rules);
            lastBranchPageWasFull = parsed.SourceItemCount == PageSize;
            if (!lastBranchPageWasFull)
            {
                break;
            }
        }

        if (lastBranchPageWasFull &&
            branchPagesFetched >= options.MaximumBranchPages)
        {
            errorKind ??= GitHubRepositoryRulesErrorKind.PageLimitReached;
        }

        ThrowIfCancellationRequested(cancellationToken);
        if (branchPagesFetched == 0 && errorKind is not null)
        {
            return UnavailableResult(
                owner,
                repositoryName,
                branch,
                errorKind.Value);
        }

        if (branchRules.Count == 0 && errorKind is null)
        {
            return KnownResult(owner, repositoryName, branch, []);
        }

        var rulesetIds = branchRules
            .Select(rule => rule.RulesetId)
            .Distinct()
            .ToArray();
        if (rulesetIds.Length > options.MaximumRulesetReads)
        {
            errorKind ??= GitHubRepositoryRulesErrorKind.RulesetLimitReached;
        }
        var rulesetDetails = new Dictionary<long, RulesetDetails?>();
        var rules = new List<GitHubCommitMessageRule>();
        foreach (var branchRule in branchRules)
        {
            ThrowIfCancellationRequested(cancellationToken);
            if (!rulesetDetails.TryGetValue(
                    branchRule.RulesetId,
                    out var ruleset))
            {
                if (rulesetDetails.Count >= options.MaximumRulesetReads)
                {
                    rulesetDetails[branchRule.RulesetId] = null;
                    continue;
                }

                var fetched = await GetRulesetAsync(
                        session,
                        owner,
                        repositoryName,
                        branchRule.RulesetId,
                        cancellationToken)
                    .ConfigureAwait(false);
                ruleset = fetched.Details;
                errorKind ??= fetched.ErrorKind;
                rulesetDetails[branchRule.RulesetId] = ruleset;
            }

            if (ruleset is null)
            {
                continue;
            }

            rules.Add(new GitHubCommitMessageRule(
                branchRule.RulesetId,
                branchRule.Operator,
                branchRule.Negate,
                branchRule.Pattern,
                ruleset.BypassMode));
        }

        ThrowIfCancellationRequested(cancellationToken);
        return errorKind is null
            ? KnownResult(owner, repositoryName, branch, rules)
            : PartialResult(owner, repositoryName, branch, rules, errorKind.Value);
    }

    private async Task<RulesetFetchResult> GetRulesetAsync(
        GitHubAccountSession session,
        string owner,
        string repositoryName,
        long rulesetId,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildRulesetEndpoint(
            options.ApiOrigin,
            owner,
            repositoryName,
            rulesetId);
        GitHubApiTransportResponse response;
        try
        {
            response = await transport.GetAsync(
                    endpoint,
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubApiTransportException exception)
        {
            if (exception.Kind == GitHubApiTransportErrorKind.Cancelled)
            {
                throw new GitHubRepositoryRulesCancelledException();
            }

            return new RulesetFetchResult(
                null,
                MapTransportError(exception.Kind));
        }

        if ((int)response.StatusCode is < 200 or >= 300)
        {
            return new RulesetFetchResult(
                null,
                MapRulesetResponseError(response));
        }

        try
        {
            return new RulesetFetchResult(
                ParseRuleset(response.Content, rulesetId),
                null);
        }
        catch (GitHubRepositoryRulesException exception)
        {
            return new RulesetFetchResult(null, exception.Kind);
        }
    }

    private static BranchPageParseResult ParseBranchRules(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 20 });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > MaximumRuleCount)
            {
                throw InvalidResponse();
            }

            var rules = new List<BranchRule>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String)
                {
                    throw InvalidResponse();
                }

                if (!string.Equals(
                        type.GetString(),
                        "commit_message_pattern",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var rulesetId = RequiredPositiveInteger(element, "ruleset_id");
                if (!element.TryGetProperty("parameters", out var parameters) ||
                    parameters.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidResponse();
                }

                var negate = OptionalBoolean(parameters, "negate");
                var operatorKind = ParseOperator(
                    RequiredText(parameters, "operator", 64));
                var pattern = RequiredPattern(
                    parameters,
                    "pattern",
                    MaximumPatternLength);
                rules.Add(new BranchRule(
                    rulesetId,
                    operatorKind,
                    negate,
                    pattern));
            }

            return new BranchPageParseResult(
                rules,
                document.RootElement.GetArrayLength());
        }
        catch (GitHubRepositoryRulesException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private static RulesetDetails ParseRuleset(string content, long expectedId)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions { MaxDepth = 20 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse();
            }

            var id = RequiredPositiveInteger(root, "id");
            if (id != expectedId)
            {
                throw InvalidResponse();
            }

            var bypassMode = RequiredText(
                root,
                "current_user_can_bypass",
                64) switch
            {
                "always" => GitHubCommitMessageRuleBypassMode.Always,
                "pull_requests_only" =>
                    GitHubCommitMessageRuleBypassMode.PullRequestsOnly,
                "never" => GitHubCommitMessageRuleBypassMode.Never,
                _ => throw InvalidResponse(),
            };
            return new RulesetDetails(bypassMode);
        }
        catch (GitHubRepositoryRulesException)
        {
            throw;
        }
        catch
        {
            throw InvalidResponse();
        }
    }

    private static GitHubCommitMessageRuleOperator ParseOperator(string value) =>
        value switch
        {
            "starts_with" => GitHubCommitMessageRuleOperator.StartsWith,
            "ends_with" => GitHubCommitMessageRuleOperator.EndsWith,
            "contains" => GitHubCommitMessageRuleOperator.Contains,
            "regex" => GitHubCommitMessageRuleOperator.Regex,
            _ => throw InvalidResponse(),
        };

    private static string ValidateRepositoryPart(
        string value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 ||
            value.Length > MaximumOwnerOrRepositoryLength ||
            value is "." or ".." ||
            value.Any(character =>
                character <= '\u001F' ||
                character == '\u007F' ||
                char.IsWhiteSpace(character) ||
                character is '/' or '\\' or '?' or '#' or ':'))
        {
            throw new GitHubRepositoryRulesException(
                GitHubRepositoryRulesErrorKind.InvalidConfiguration);
        }

        return value;
    }

    private static string ValidateBranch(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 ||
            value.Length > MaximumBranchLength ||
            value is "." or ".." ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            value.Any(character =>
                character <= '\u001F' ||
                character == '\u007F' ||
                char.IsWhiteSpace(character) ||
                character is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            throw new GitHubRepositoryRulesException(
                GitHubRepositoryRulesErrorKind.InvalidConfiguration);
        }

        return value;
    }

    private static Uri BuildBranchEndpoint(
        Uri apiOrigin,
        string owner,
        string repositoryName,
        string branch,
        int pageNumber) =>
        new(
            apiOrigin,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}/rules/branches/{Uri.EscapeDataString(branch)}?per_page={PageSize}&page={pageNumber}");

    private static Uri BuildRulesetEndpoint(
        Uri apiOrigin,
        string owner,
        string repositoryName,
        long rulesetId) =>
        new(
            apiOrigin,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}/rulesets/{rulesetId.ToString(CultureInfo.InvariantCulture)}");

    private static long RequiredPositiveInteger(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result) ||
            result <= 0)
        {
            throw InvalidResponse();
        }

        return result;
    }

    private static bool OptionalBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidResponse();
        }

        return value.GetBoolean();
    }

    private static string RequiredText(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }

        var result = value.GetString();
        if (result is null ||
            result.Length == 0 ||
            result.Length > maximumLength ||
            result.Any(character =>
                character <= '\u001F' ||
                character == '\u007F' ||
                char.IsWhiteSpace(character)))
        {
            throw InvalidResponse();
        }

        return result;
    }

    private static string RequiredPattern(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }

        var result = value.GetString();
        if (result is null ||
            result.Length == 0 ||
            result.Length > maximumLength ||
            result.Any(character =>
                character <= '\u001F' || character == '\u007F'))
        {
            throw InvalidResponse();
        }

        return result;
    }

    private GitHubRepositoryRulesResult KnownResult(
        string owner,
        string repositoryName,
        string branch,
        IReadOnlyList<GitHubCommitMessageRule> rules) =>
        new(
            options.ApiOrigin,
            owner,
            repositoryName,
            branch,
            rules.ToArray(),
            GitHubRepositoryRulesAvailability.Known,
            null);

    private GitHubRepositoryRulesResult PartialResult(
        string owner,
        string repositoryName,
        string branch,
        IReadOnlyList<GitHubCommitMessageRule> rules,
        GitHubRepositoryRulesErrorKind errorKind) =>
        new(
            options.ApiOrigin,
            owner,
            repositoryName,
            branch,
            rules.ToArray(),
            GitHubRepositoryRulesAvailability.Partial,
            errorKind);

    private GitHubRepositoryRulesResult UnavailableResult(
        string owner,
        string repositoryName,
        string branch,
        GitHubRepositoryRulesErrorKind errorKind) =>
        new(
            options.ApiOrigin,
            owner,
            repositoryName,
            branch,
            [],
            GitHubRepositoryRulesAvailability.Unavailable,
            errorKind);

    private static GitHubRepositoryRulesErrorKind MapTransportError(
        GitHubApiTransportErrorKind kind) =>
        kind switch
        {
            GitHubApiTransportErrorKind.SessionMismatch =>
                GitHubRepositoryRulesErrorKind.SessionMismatch,
            GitHubApiTransportErrorKind.Timeout =>
                GitHubRepositoryRulesErrorKind.Timeout,
            GitHubApiTransportErrorKind.Cancelled =>
                GitHubRepositoryRulesErrorKind.InvalidConfiguration,
            GitHubApiTransportErrorKind.InvalidResponse =>
                GitHubRepositoryRulesErrorKind.InvalidResponse,
            _ => GitHubRepositoryRulesErrorKind.Network,
        };

    private static GitHubRepositoryRulesErrorKind MapResponseError(
        GitHubApiTransportResponse response) =>
        response.StatusCode == HttpStatusCode.NotFound
            ? GitHubRepositoryRulesErrorKind.NotFound
            : MapCommonResponseError(response);

    private static GitHubRepositoryRulesErrorKind MapRulesetResponseError(
        GitHubApiTransportResponse response) =>
        response.StatusCode == HttpStatusCode.NotFound
            ? GitHubRepositoryRulesErrorKind.NotFound
            : MapCommonResponseError(response);

    private static GitHubRepositoryRulesErrorKind MapCommonResponseError(
        GitHubApiTransportResponse response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return GitHubRepositoryRulesErrorKind.Unauthorized;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.HasSamlHeader)
        {
            return GitHubRepositoryRulesErrorKind.SamlRequired;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.IsRateLimited)
        {
            return GitHubRepositoryRulesErrorKind.RateLimited;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return GitHubRepositoryRulesErrorKind.Forbidden;
        }

        if ((int)response.StatusCode >= 500)
        {
            return GitHubRepositoryRulesErrorKind.Network;
        }

        return GitHubRepositoryRulesErrorKind.InvalidResponse;
    }

    private static GitHubRepositoryRulesException InvalidResponse() =>
        new(GitHubRepositoryRulesErrorKind.InvalidResponse);

    private static void ThrowIfCancellationRequested(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new GitHubRepositoryRulesCancelledException();
        }
    }

    private static GitHubApiTransport CreateTransport(
        GitHubRepositoryRulesClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitHubApiTransport(
            options.ApiOrigin,
            options.RequestTimeout,
            options.MaximumResponseBytes);
    }

    private static GitHubApiTransport CreateTransport(
        GitHubRepositoryRulesClientOptions options,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GitHubApiTransport(
            options.ApiOrigin,
            options.RequestTimeout,
            options.MaximumResponseBytes,
            httpClient);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        transport.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(GitHubRepositoryRulesClient));
        }
    }

    private sealed record BranchRule(
        long RulesetId,
        GitHubCommitMessageRuleOperator Operator,
        bool Negate,
        string Pattern);

    private sealed record BranchPageParseResult(
        IReadOnlyList<BranchRule> Rules,
        int SourceItemCount);

    private sealed record RulesetDetails(
        GitHubCommitMessageRuleBypassMode BypassMode);

    private sealed record RulesetFetchResult(
        RulesetDetails? Details,
        GitHubRepositoryRulesErrorKind? ErrorKind);
}
