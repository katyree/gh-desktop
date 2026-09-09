using System.Text.Json;

namespace WinGit.Core.Codex;

/// <summary>JSON-RPC method names used by the native Codex bridge.</summary>
public static class CodexAppServerMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string ReadAccount = "account/read";
    public const string ReadRateLimits = "account/rateLimits/read";
    public const string ListModels = "model/list";
    public const string LoginStart = "account/login/start";
    public const string LoginCancel = "account/login/cancel";
    public const string Logout = "account/logout";
    public const string ThreadStart = "thread/start";
    public const string TurnStart = "turn/start";
    public const string TurnInterrupt = "turn/interrupt";
    public const string CancelRequest = "$/cancelRequest";
}

/// <summary>Metadata returned by the app server's initialize handshake.</summary>
public sealed record CodexAppServerInfo(
    string CodexHome,
    string PlatformFamily,
    string PlatformOs,
    string UserAgent);

/// <summary>The account category reported by Codex.</summary>
public enum CodexAccountType
{
    SignedOut,
    ChatGpt,
    ApiKey,
    Other,
}

/// <summary>The safe account state exposed by the native client.</summary>
public enum CodexAccountStatus
{
    SignedOut,
    SignedIn,
    Unavailable,
}

/// <summary>Credential-free account metadata returned by <c>account/read</c>.</summary>
public sealed record CodexAccountState(
    CodexAccountStatus Status,
    CodexAccountType Type,
    string? Email,
    string? PlanType,
    bool RequiresOpenAiAuth)
{
    public static CodexAccountState UnavailableState { get; } = new(
        CodexAccountStatus.Unavailable,
        CodexAccountType.SignedOut,
        Email: null,
        PlanType: null,
        RequiresOpenAiAuth: true);
}

/// <summary>The deliberate account login flow selected by native UI.</summary>
public enum CodexLoginMethod
{
    Browser,
    DeviceCode,
}

/// <summary>
/// Typed login material returned to the UI. The diagnostic representation
/// intentionally omits the authorization URL, device code, and opaque id.
/// </summary>
public sealed record CodexLoginStart(
    CodexLoginMethod Method,
    string LoginId,
    string AuthorizationUrl,
    string? UserCode)
{
    public override string ToString() => $"Codex login started ({Method}).";
}

/// <summary>
/// Safe completion status from an account login notification. The optional
/// opaque id lets native UI correlate a completion with its active login
/// without exposing server error or credential payloads.
/// </summary>
public sealed record CodexLoginCompletion(
    string? LoginId,
    bool Success)
{
    public override string ToString() =>
        $"Codex login completed ({(Success ? "success" : "failure")}).";
}

/// <summary>Rate-limit status suitable for a native status indicator.</summary>
public enum CodexRateLimitStatus
{
    Available,
    NearLimit,
    Exhausted,
    Unavailable,
}

/// <summary>One rolling rate-limit window.</summary>
public sealed record CodexRateLimitWindow(
    double UsedPercent,
    DateTimeOffset? ResetsAt);

/// <summary>The stable legacy single-bucket rate-limit view.</summary>
public sealed record CodexRateLimitState(
    CodexRateLimitStatus Status,
    CodexRateLimitWindow? Primary,
    CodexRateLimitWindow? Secondary,
    DateTimeOffset? ResetsAt)
{
    public static CodexRateLimitState UnavailableState { get; } = new(
        CodexRateLimitStatus.Unavailable,
        Primary: null,
        Secondary: null,
        ResetsAt: null);
}

/// <summary>One reasoning effort advertised by a Codex model.</summary>
public sealed record CodexReasoningEffort(
    string ReasoningEffort,
    string Description);

/// <summary>A visible model entry returned by the app server catalog.</summary>
public sealed record CodexModel(
    string Id,
    string Model,
    string DisplayName,
    string Description,
    bool IsDefault,
    string DefaultReasoningEffort,
    IReadOnlyList<CodexReasoningEffort> SupportedReasoningEfforts);

/// <summary>One page from the app server's paginated model catalog.</summary>
public sealed record CodexModelPage(
    IReadOnlyList<CodexModel> Models,
    string? NextCursor);

/// <summary>
/// One read-only, ephemeral Codex turn. The model and reasoning effort are
/// optional so the server can apply its selected defaults.
/// </summary>
public sealed record CodexGenerationRequest(
    string Instructions,
    string Prompt,
    string? Model = null,
    string? ReasoningEffort = null,
    JsonElement? OutputSchema = null)
{
    public override string ToString() => "Codex generation request.";
}

/// <summary>Opaque identifiers for one ephemeral Codex turn.</summary>
public sealed record CodexGenerationHandle(
    string ThreadId,
    string TurnId)
{
    public override string ToString() => "Codex generation handle.";
}

/// <summary>Sanitized terminal outcomes for one generation turn.</summary>
public enum CodexGenerationOutcome
{
    Success,
    Cancelled,
    AuthRequired,
    RateLimited,
    Timeout,
    RuntimeError,
}

/// <summary>Terminal output from one generation turn.</summary>
public sealed record CodexGenerationResult(
    CodexGenerationOutcome Outcome,
    string? Output)
{
    public override string ToString() =>
        $"Codex generation completed ({Outcome}).";
}

/// <summary>Progress that is safe to surface while a turn is running.</summary>
public enum CodexGenerationProgressKind
{
    TurnStarted,
    ItemStarted,
    ItemCompleted,
    TurnCompleted,
}

/// <summary>
/// Sanitized generation lifecycle progress. Item content and server error
/// payloads are deliberately omitted; completion carries only the typed result.
/// </summary>
public sealed record CodexGenerationProgress(
    CodexGenerationProgressKind Kind,
    string? ThreadId,
    string? TurnId,
    string? ItemType,
    CodexGenerationResult? Completion = null)
{
    public override string ToString() =>
        $"Codex generation progress ({Kind}).";
}

/// <summary>Known notification categories crossing the native boundary.</summary>
public enum CodexNotificationKind
{
    Unknown,
    AccountUpdated,
    RateLimitsUpdated,
    LoginCompleted,
    GenerationProgress,
}

/// <summary>
/// A sanitized server notification. Unknown payload fields are deliberately
/// omitted so account and rate-limit notifications cannot carry credentials into
/// native UI state.
/// </summary>
public sealed record CodexServerNotification(
    string Method,
    CodexNotificationKind Kind,
    string? AuthMode,
    string? PlanType,
    CodexRateLimitState? RateLimits,
    CodexLoginCompletion? LoginCompletion = null,
    CodexGenerationProgress? GenerationProgress = null);

/// <summary>Options for one owned local app-server process.</summary>
public sealed record CodexAppServerClientOptions
{
    /// <summary>Optional absolute path; when omitted, the packaged/PATH locator is used.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Optional Codex home override for tests or an explicitly selected native
    /// profile. When omitted, `%LOCALAPPDATA%\\WinGit.Native\\codex` is used.
    /// </summary>
    public string? CodexHomePath { get; init; }

    /// <summary>Optional application root containing the packaged Codex runtime.</summary>
    public string? ApplicationRoot { get; init; }

    /// <summary>Working directory for the app-server process.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Optional isolated workspace for ephemeral generation. When omitted, the
    /// native Codex profile's <c>empty-workspace</c> directory is used.
    /// </summary>
    public string? GenerationWorkingDirectory { get; init; }

    public string ClientName { get; init; } = "wingit";

    public string ClientTitle { get; init; } = "WinGit";

    public string ClientVersion { get; init; } = "0.1.0";

    /// <summary>Codex's standard local stdio entry point.</summary>
    public IReadOnlyList<string> Arguments { get; init; } =
        ["app-server", "--stdio"];

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time a caller may wait for one generation turn.</summary>
    public TimeSpan GenerationTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaximumLineBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Opt into the existing experimental fields during initialize.</summary>
    public bool ExperimentalApi { get; init; } = true;
}

/// <summary>The owned process lifecycle visible to the native host.</summary>
public enum CodexAppServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

internal enum CodexRpcMessageKind
{
    Response,
    Notification,
    ServerRequest,
}

internal sealed record CodexRpcError(int Code);

internal sealed record CodexRpcMessage(
    CodexRpcMessageKind Kind,
    long? Id,
    string Method,
    JsonElement? Parameters,
    JsonElement? Result,
    CodexRpcError? Error);

/// <summary>
/// Pure parsing and framing helpers for Codex's newline-delimited JSON-RPC
/// protocol. Process lifecycle and request correlation live in the client.
/// </summary>
public static class CodexAppServerProtocol
{
    private static readonly HashSet<string> CodexAuthorizationHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "auth.openai.com",
        "chatgpt.com",
        "www.chatgpt.com",
    };

    private const int MaximumAuthorizationUrlLength = 16_384;

    public static CodexAppServerInfo ParseInitializeResponse(string json)
    {
        using var document = ParseDocument(json, "initialize response");
        return ParseInitializeResponse(document.RootElement);
    }

    public static CodexAccountState ParseAccountResponse(string json)
    {
        using var document = ParseDocument(json, "account response");
        return ParseAccountResponse(document.RootElement);
    }

    public static CodexRateLimitState ParseRateLimitsResponse(string json)
    {
        using var document = ParseDocument(json, "rate-limit response");
        return ParseRateLimitsResponse(document.RootElement);
    }

    public static CodexModelPage ParseModelListResponse(string json)
    {
        using var document = ParseDocument(json, "model catalog response");
        return ParseModelListResponse(document.RootElement);
    }

    /// <summary>Parses and validates one deliberate account login start.</summary>
    public static CodexLoginStart ParseLoginStartResponse(
        string json,
        CodexLoginMethod method)
    {
        using var document = ParseDocument(json, "login response");
        return ParseLoginStartResponse(document.RootElement, method);
    }

    /// <summary>
    /// Accepts only official HTTPS login hosts before a URL reaches native UI
    /// or an operating-system browser launcher.
    /// </summary>
    public static string ValidateAuthorizationUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumAuthorizationUrlLength ||
            !Uri.TryCreate(value, UriKind.Absolute, out var authorizationUri))
        {
            throw new CodexAppServerProtocolException(
                "Codex returned an invalid authorization URL.");
        }

        if (!string.Equals(
                authorizationUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            authorizationUri.UserInfo.Length > 0 ||
            !CodexAuthorizationHosts.Contains(authorizationUri.Host))
        {
            throw new CodexAppServerProtocolException(
                "Codex returned an untrusted authorization URL.");
        }

        return authorizationUri.ToString();
    }

    internal static CodexAppServerInfo ParseInitializeResponse(JsonElement root)
    {
        RequireObject(root, "initialize response");
        return new CodexAppServerInfo(
            RequiredString(root, "codexHome", "initialize response"),
            RequiredString(root, "platformFamily", "initialize response"),
            RequiredString(root, "platformOs", "initialize response"),
            RequiredString(root, "userAgent", "initialize response"));
    }

    internal static CodexAccountState ParseAccountResponse(JsonElement root)
    {
        RequireObject(root, "account response");
        if (!root.TryGetProperty("requiresOpenaiAuth", out var requiresAuth) ||
            requiresAuth.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new CodexAppServerProtocolException(
                "Codex account response omitted requiresOpenaiAuth.");
        }

        if (!root.TryGetProperty("account", out var account) ||
            account.ValueKind == JsonValueKind.Null)
        {
            return new CodexAccountState(
                CodexAccountStatus.SignedOut,
                CodexAccountType.SignedOut,
                Email: null,
                PlanType: null,
                requiresAuth.GetBoolean());
        }

        RequireObject(account, "account");
        var type = RequiredString(account, "type", "account");
        return type switch
        {
            "chatgpt" => new CodexAccountState(
                CodexAccountStatus.SignedIn,
                CodexAccountType.ChatGpt,
                OptionalString(account, "email", 512),
                OptionalString(account, "planType", 200),
                requiresAuth.GetBoolean()),
            "apiKey" => new CodexAccountState(
                CodexAccountStatus.SignedIn,
                CodexAccountType.ApiKey,
                Email: null,
                PlanType: null,
                requiresAuth.GetBoolean()),
            _ => new CodexAccountState(
                CodexAccountStatus.SignedIn,
                CodexAccountType.Other,
                Email: null,
                PlanType: null,
                requiresAuth.GetBoolean()),
        };
    }

    internal static CodexRateLimitState ParseRateLimitsResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("rateLimits", out var snapshot) ||
            snapshot.ValueKind != JsonValueKind.Object)
        {
            return CodexRateLimitState.UnavailableState;
        }

        var primaryResult = ParseWindow(snapshot, "primary");
        var secondaryResult = ParseWindow(snapshot, "secondary");
        if (!primaryResult.IsValid || !secondaryResult.IsValid)
        {
            return CodexRateLimitState.UnavailableState;
        }

        var primary = primaryResult.Value;
        var secondary = secondaryResult.Value;
        if (primary is null && secondary is null)
        {
            return CodexRateLimitState.UnavailableState;
        }

        var highestUsedPercent = Math.Max(
            primary?.UsedPercent ?? 0,
            secondary?.UsedPercent ?? 0);
        var explicitlyExhausted =
            (snapshot.TryGetProperty("spendControlReached", out var spendControl) &&
             spendControl.ValueKind == JsonValueKind.True) ||
            (snapshot.TryGetProperty("rateLimitReachedType", out var reachedType) &&
             reachedType.ValueKind == JsonValueKind.String &&
             !string.IsNullOrWhiteSpace(reachedType.GetString()));

        var status = explicitlyExhausted || highestUsedPercent >= 100
            ? CodexRateLimitStatus.Exhausted
            : highestUsedPercent >= 80
                ? CodexRateLimitStatus.NearLimit
                : CodexRateLimitStatus.Available;

        var resetTimes = new[]
        {
            primary?.ResetsAt,
            secondary?.ResetsAt,
        }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();

        return new CodexRateLimitState(
            status,
            primary,
            secondary,
            resetTimes.Length == 0 ? null : resetTimes[0]);
    }

    internal static CodexModelPage ParseModelListResponse(JsonElement root)
    {
        RequireObject(root, "model catalog response");
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw new CodexAppServerProtocolException(
                "Codex model catalog omitted data.");
        }

        var models = new List<CodexModel>();
        foreach (var value in data.EnumerateArray())
        {
            RequireObject(value, "model entry");
            var id = RequiredString(value, "id", "model entry", 200);
            var model = RequiredString(value, "model", "model entry", 200);
            var displayName = RequiredString(value, "displayName", "model entry", 200);
            var description = BoundedString(value, "description", "model entry", 2_000);
            var hidden = RequiredBoolean(value, "hidden", "model entry");
            var isDefault = RequiredBoolean(value, "isDefault", "model entry");
            var defaultReasoningEffort = RequiredString(
                value,
                "defaultReasoningEffort",
                "model entry",
                200);

            if (!value.TryGetProperty("supportedReasoningEfforts", out var effortValues) ||
                effortValues.ValueKind != JsonValueKind.Array)
            {
                throw new CodexAppServerProtocolException(
                    "Codex model entry omitted supportedReasoningEfforts.");
            }

            var efforts = new List<CodexReasoningEffort>();
            foreach (var effort in effortValues.EnumerateArray())
            {
                RequireObject(effort, "reasoning effort");
                efforts.Add(new CodexReasoningEffort(
                    RequiredString(effort, "reasoningEffort", "reasoning effort", 200),
                    BoundedString(effort, "description", "reasoning effort", 1_000)));
            }

            // Validate the complete server entry before filtering hidden models.
            if (!hidden)
            {
                models.Add(new CodexModel(
                    id,
                    model,
                    displayName,
                    description,
                    isDefault,
                    defaultReasoningEffort,
                    efforts.AsReadOnly()));
            }
        }

        string? nextCursor = null;
        if (root.TryGetProperty("nextCursor", out var cursor) &&
            cursor.ValueKind != JsonValueKind.Null)
        {
            nextCursor = cursor.ValueKind == JsonValueKind.String
                ? cursor.GetString()
                : throw new CodexAppServerProtocolException(
                    "Codex model catalog returned an invalid nextCursor.");
            if (string.IsNullOrEmpty(nextCursor))
            {
                throw new CodexAppServerProtocolException(
                    "Codex model catalog returned an empty nextCursor.");
            }
        }

        return new CodexModelPage(models.AsReadOnly(), nextCursor);
    }

    internal static CodexLoginStart ParseLoginStartResponse(
        JsonElement root,
        CodexLoginMethod method)
    {
        RequireObject(root, "login response");
        var loginId = RequiredString(root, "loginId", "login response", 200);

        return method switch
        {
            CodexLoginMethod.Browser => ParseBrowserLoginStart(root, loginId),
            CodexLoginMethod.DeviceCode => ParseDeviceCodeLoginStart(root, loginId),
            _ => throw new CodexAppServerProtocolException(
                "Codex returned an unsupported login method."),
        };
    }

    internal static string ParseThreadStartResponse(JsonElement root)
    {
        return ParseGenerationStartId(root, "thread", "thread/start response");
    }

    internal static string ParseTurnStartResponse(JsonElement root)
    {
        return ParseGenerationStartId(root, "turn", "turn/start response");
    }

    internal static void ValidateGenerationRequest(CodexGenerationRequest? request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateRequiredGenerationString(request.Instructions, nameof(request.Instructions));
        ValidateRequiredGenerationString(request.Prompt, nameof(request.Prompt));
        ValidateOptionalGenerationString(request.Model, nameof(request.Model));
        ValidateOptionalGenerationString(
            request.ReasoningEffort,
            nameof(request.ReasoningEffort));

        if (request.OutputSchema is not JsonElement schema)
        {
            return;
        }

        ValidateJsonValue(schema, depth: 0);
        if (schema.GetRawText().Length > 262_144)
        {
            throw new ArgumentException(
                "outputSchema exceeds 262144 characters.",
                nameof(request));
        }
    }

    public static CodexServerNotification ParseNotification(
        string method,
        string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new CodexAppServerProtocolException(
                "Codex notification omitted its method.");
        }

        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return ParseNotification(method, parameters: null);
        }

        using var document = ParseDocument(parametersJson, "notification parameters");
        return ParseNotification(method, document.RootElement);
    }

    internal static CodexServerNotification ParseNotification(
        string method,
        JsonElement? parameters)
    {
        if (string.Equals(
                method,
                "account/updated",
                StringComparison.Ordinal))
        {
            return new CodexServerNotification(
                method,
                CodexNotificationKind.AccountUpdated,
                parameters is { ValueKind: JsonValueKind.Object }
                    ? OptionalString(parameters.Value, "authMode", 200)
                    : null,
                parameters is { ValueKind: JsonValueKind.Object }
                    ? OptionalString(parameters.Value, "planType", 200)
                    : null,
                RateLimits: null);
        }

        if (string.Equals(
                method,
                "account/rateLimits/updated",
                StringComparison.Ordinal))
        {
            var rateLimits = parameters is { ValueKind: JsonValueKind.Object }
                ? ParseRateLimitsResponse(parameters.Value)
                : CodexRateLimitState.UnavailableState;
            return new CodexServerNotification(
                method,
                CodexNotificationKind.RateLimitsUpdated,
                AuthMode: null,
                PlanType: null,
                rateLimits);
        }

        if (string.Equals(
                method,
                "account/login/completed",
                StringComparison.Ordinal))
        {
            CodexLoginCompletion? completion = null;
            if (parameters is { ValueKind: JsonValueKind.Object } &&
                parameters.Value.TryGetProperty("success", out var success) &&
                success.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                string? loginId = null;
                if (parameters.Value.TryGetProperty("loginId", out var loginIdValue) &&
                    loginIdValue.ValueKind == JsonValueKind.String)
                {
                    var candidate = loginIdValue.GetString();
                    if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 200)
                    {
                        loginId = candidate;
                    }
                }

                completion = new CodexLoginCompletion(loginId, success.GetBoolean());
            }

            return new CodexServerNotification(
                method,
                CodexNotificationKind.LoginCompleted,
                AuthMode: null,
                PlanType: null,
                RateLimits: null,
                LoginCompletion: completion);
        }

        var generationProgress = ParseGenerationProgress(method, parameters);
        if (generationProgress is not null)
        {
            return new CodexServerNotification(
                method,
                CodexNotificationKind.GenerationProgress,
                AuthMode: null,
                PlanType: null,
                RateLimits: null,
                GenerationProgress: generationProgress);
        }

        return new CodexServerNotification(
            method,
            CodexNotificationKind.Unknown,
            AuthMode: null,
            PlanType: null,
                RateLimits: null);
    }

    private static string ParseGenerationStartId(
        JsonElement root,
        string container,
        string context)
    {
        RequireObject(root, context);
        if (!root.TryGetProperty(container, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new CodexAppServerProtocolException(
                $"Codex {context} omitted {container}.");
        }

        return RequiredString(value, "id", $"{context} {container}", 200);
    }

    private static CodexGenerationProgress? ParseGenerationProgress(
        string method,
        JsonElement? parameters)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parameterObject = parameters.Value;

        if (string.Equals(method, "turn/started", StringComparison.Ordinal))
        {
            if (!parameterObject.TryGetProperty("turn", out var turn) ||
                turn.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var turnId = OptionalIdentifier(turn, "id");
            return turnId is null
                ? null
                : new CodexGenerationProgress(
                    CodexGenerationProgressKind.TurnStarted,
                    OptionalIdentifier(parameterObject, "threadId"),
                    turnId,
                    ItemType: null);
        }

        if (string.Equals(method, "item/started", StringComparison.Ordinal) ||
            string.Equals(method, "item/completed", StringComparison.Ordinal))
        {
            if (!parameterObject.TryGetProperty("item", out var item) ||
                item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var kind = string.Equals(method, "item/started", StringComparison.Ordinal)
                ? CodexGenerationProgressKind.ItemStarted
                : CodexGenerationProgressKind.ItemCompleted;
            return new CodexGenerationProgress(
                kind,
                OptionalIdentifier(parameterObject, "threadId"),
                OptionalIdentifier(parameterObject, "turnId"),
                OptionalIdentifier(item, "type"));
        }

        if (!string.Equals(method, "turn/completed", StringComparison.Ordinal))
        {
            return null;
        }

        return ParseCompletedTurnProgress(parameterObject);
    }

    private static CodexGenerationProgress? ParseCompletedTurnProgress(
        JsonElement parameters)
    {
        var threadId = OptionalIdentifier(parameters, "threadId");
        if (threadId is null ||
            !parameters.TryGetProperty("turn", out var turn) ||
            turn.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var turnId = OptionalIdentifier(turn, "id");
        var status = OptionalIdentifier(turn, "status", 100);
        if (turnId is null || status is null)
        {
            return null;
        }

        var result = status switch
        {
            "interrupted" => new CodexGenerationResult(
                CodexGenerationOutcome.Cancelled,
                Output: null),
            "failed" => new CodexGenerationResult(
                MapGenerationFailure(turn),
                Output: null),
            "completed" => ParseCompletedTurnResult(turn),
            _ => new CodexGenerationResult(
                CodexGenerationOutcome.RuntimeError,
                Output: null),
        };

        return new CodexGenerationProgress(
            CodexGenerationProgressKind.TurnCompleted,
            threadId,
            turnId,
            ItemType: null,
            Completion: result);
    }

    private static CodexGenerationResult ParseCompletedTurnResult(JsonElement turn)
    {
        if (!turn.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return new CodexGenerationResult(
                CodexGenerationOutcome.RuntimeError,
                Output: null);
        }

        string? finalAnswer = null;
        string? unphasedMessage = null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !string.Equals(
                    OptionalIdentifier(item, "type"),
                    "agentMessage",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var text = OptionalString(item, "text", 1_000_000);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var phase = OptionalString(item, "phase", 200);
            if (string.Equals(phase, "final_answer", StringComparison.Ordinal))
            {
                finalAnswer = text;
            }
            else if (phase is null)
            {
                unphasedMessage = text;
            }
        }

        var output = finalAnswer ?? unphasedMessage;
        return output is null
            ? new CodexGenerationResult(
                CodexGenerationOutcome.RuntimeError,
                Output: null)
            : new CodexGenerationResult(
                CodexGenerationOutcome.Success,
                output);
    }

    private static CodexGenerationOutcome MapGenerationFailure(JsonElement turn)
    {
        if (!turn.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object)
        {
            return CodexGenerationOutcome.RuntimeError;
        }

        var errorInfo = OptionalString(error, "codexErrorInfo", 200);
        if (string.Equals(errorInfo, "unauthorized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorInfo, "authentication", StringComparison.OrdinalIgnoreCase))
        {
            return CodexGenerationOutcome.AuthRequired;
        }

        if (string.Equals(errorInfo, "usageLimitExceeded", StringComparison.Ordinal) ||
            string.Equals(errorInfo, "rateLimitExceeded", StringComparison.Ordinal) ||
            string.Equals(errorInfo, "sessionBudgetExceeded", StringComparison.Ordinal))
        {
            return CodexGenerationOutcome.RateLimited;
        }

        if (HasHttpStatus(error, 401))
        {
            return CodexGenerationOutcome.AuthRequired;
        }

        if (HasHttpStatus(error, 429))
        {
            return CodexGenerationOutcome.RateLimited;
        }

        var message = OptionalString(error, "message", 4_000);
        if (message is not null)
        {
            if (message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("401", StringComparison.Ordinal))
            {
                return CodexGenerationOutcome.AuthRequired;
            }

            if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("429", StringComparison.Ordinal))
            {
                return CodexGenerationOutcome.RateLimited;
            }
        }

        return CodexGenerationOutcome.RuntimeError;
    }

    private static bool HasHttpStatus(JsonElement parent, int expectedStatus)
    {
        if (parent.TryGetProperty("httpStatusCode", out var directStatus) &&
            directStatus.TryGetInt32(out var directValue) &&
            directValue == expectedStatus)
        {
            return true;
        }

        if (!parent.TryGetProperty("codexErrorInfo", out var errorInfo) ||
            errorInfo.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in errorInfo.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("httpStatusCode", out var status) &&
                status.TryGetInt32(out var value) &&
                value == expectedStatus)
            {
                return true;
            }
        }

        return false;
    }

    private static string? OptionalIdentifier(
        JsonElement parent,
        string propertyName,
        int maximumLength = 200)
    {
        var value = OptionalString(parent, propertyName, maximumLength);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void ValidateRequiredGenerationString(
        string? value,
        string propertyName)
    {
        if (value is null || value.Length == 0 || value.Length > 1_000_000)
        {
            throw new ArgumentException(
                $"{propertyName} must be a non-empty value of at most 1000000 characters.",
                propertyName);
        }
    }

    private static void ValidateOptionalGenerationString(
        string? value,
        string propertyName)
    {
        if (value is not null && (string.IsNullOrEmpty(value) || value.Length > 200))
        {
            throw new ArgumentException(
                $"{propertyName} must be at most 200 characters when provided.",
                propertyName);
        }
    }

    private static void ValidateJsonValue(JsonElement value, int depth)
    {
        if (depth > 50)
        {
            throw new ArgumentException(
                "outputSchema exceeds the maximum nesting depth.",
                "request");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.String:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return;
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    throw new ArgumentException(
                        "outputSchema contains a non-finite number.",
                        "request");
                }

                return;
            case JsonValueKind.Array:
                foreach (var entry in value.EnumerateArray())
                {
                    ValidateJsonValue(entry, depth + 1);
                }

                return;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    ValidateJsonValue(property.Value, depth + 1);
                }

                return;
            default:
                throw new ArgumentException(
                    "outputSchema contains a non-JSON value.",
                    "request");
        }
    }

    private static CodexLoginStart ParseBrowserLoginStart(
        JsonElement root,
        string loginId)
    {
        if (!string.Equals(
                RequiredString(root, "type", "browser login response", 200),
                "chatgpt",
                StringComparison.Ordinal))
        {
            throw new CodexAppServerProtocolException(
                "Codex returned the wrong browser login type.");
        }

        return new CodexLoginStart(
            CodexLoginMethod.Browser,
            loginId,
            ValidateAuthorizationUrl(RequiredString(
                root,
                "authUrl",
                "browser login response",
                MaximumAuthorizationUrlLength)),
            UserCode: null);
    }

    private static CodexLoginStart ParseDeviceCodeLoginStart(
        JsonElement root,
        string loginId)
    {
        if (!string.Equals(
                RequiredString(root, "type", "device login response", 200),
                "chatgptDeviceCode",
                StringComparison.Ordinal))
        {
            throw new CodexAppServerProtocolException(
                "Codex returned the wrong device login type.");
        }

        return new CodexLoginStart(
            CodexLoginMethod.DeviceCode,
            loginId,
            ValidateAuthorizationUrl(RequiredString(
                root,
                "verificationUrl",
                "device login response",
                MaximumAuthorizationUrlLength)),
            RequiredString(root, "userCode", "device login response", 100));
    }

    internal static CodexRpcMessage ParseRpcMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new CodexAppServerProtocolException(
                "Codex App Server emitted an empty JSON-RPC line.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new CodexAppServerProtocolException(
                "Codex App Server emitted malformed JSON.",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            RequireObject(root, "JSON-RPC message");

            if (root.TryGetProperty("method", out var methodValue))
            {
                if (methodValue.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(methodValue.GetString()))
                {
                    throw new CodexAppServerProtocolException(
                        "Codex App Server emitted an invalid method.");
                }

                var method = methodValue.GetString()!;
                var parameters = root.TryGetProperty("params", out var paramsValue)
                    ? paramsValue.Clone()
                    : (JsonElement?)null;

                if (!root.TryGetProperty("id", out var idValue))
                {
                    return new CodexRpcMessage(
                        CodexRpcMessageKind.Notification,
                        Id: null,
                        method,
                        parameters,
                        Result: null,
                        Error: null);
                }

                return new CodexRpcMessage(
                    CodexRpcMessageKind.ServerRequest,
                    ParseId(idValue),
                    method,
                    parameters,
                    Result: null,
                    Error: null);
            }

            if (!root.TryGetProperty("id", out var responseId))
            {
                throw new CodexAppServerProtocolException(
                    "Codex App Server emitted an unrecognized JSON-RPC message.");
            }

            var id = ParseId(responseId);
            var hasResult = root.TryGetProperty("result", out var result);
            var hasError = root.TryGetProperty("error", out var error);
            if (hasResult == hasError)
            {
                throw new CodexAppServerProtocolException(
                    "Codex App Server emitted an invalid JSON-RPC response envelope.");
            }

            if (hasError)
            {
                if (error.ValueKind != JsonValueKind.Object ||
                    !error.TryGetProperty("code", out var codeValue) ||
                    !codeValue.TryGetInt32(out var code) ||
                    !error.TryGetProperty("message", out var messageValue) ||
                    messageValue.ValueKind != JsonValueKind.String)
                {
                    throw new CodexAppServerProtocolException(
                        "Codex App Server emitted an invalid JSON-RPC error.");
                }

                // Do not retain server-provided error text: it can contain
                // credentials or remote URLs. The request method supplies the
                // user-facing context at the boundary.
                return new CodexRpcMessage(
                    CodexRpcMessageKind.Response,
                    id,
                    Method: string.Empty,
                    Parameters: null,
                    Result: null,
                    Error: new CodexRpcError(code));
            }

            return new CodexRpcMessage(
                CodexRpcMessageKind.Response,
                id,
                Method: string.Empty,
                Parameters: null,
                Result: result.Clone(),
                Error: null);
        }
    }

    internal static string SerializeRequest(
        long id,
        string method,
        object? parameters)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            envelope["params"] = parameters;
        }

        return JsonSerializer.Serialize(envelope);
    }

    internal static string SerializeNotification(string method, object? parameters)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["method"] = method,
        };
        if (parameters is not null)
        {
            envelope["params"] = parameters;
        }

        return JsonSerializer.Serialize(envelope);
    }

    private static JsonDocument ParseDocument(string json, string context)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new CodexAppServerProtocolException($"Codex returned an empty {context}.");
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new CodexAppServerProtocolException(
                $"Codex returned malformed {context}.",
                exception);
        }
    }

    private static ParsedRateLimitWindow ParseWindow(JsonElement snapshot, string name)
    {
        if (!snapshot.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return new ParsedRateLimitWindow(IsValid: true, Value: null);
        }

        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("usedPercent", out var usedPercentValue) ||
            !usedPercentValue.TryGetDouble(out var usedPercent) ||
            !double.IsFinite(usedPercent) ||
            usedPercent < 0)
        {
            return new ParsedRateLimitWindow(IsValid: false, Value: null);
        }

        if (!value.TryGetProperty("resetsAt", out var resetValue))
        {
            return new ParsedRateLimitWindow(IsValid: false, Value: null);
        }

        DateTimeOffset? resetsAt = null;
        if (resetValue.ValueKind != JsonValueKind.Null)
        {
            if (!resetValue.TryGetInt64(out var unixSeconds) || unixSeconds <= 0)
            {
                return new ParsedRateLimitWindow(IsValid: false, Value: null);
            }

            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new ParsedRateLimitWindow(IsValid: false, Value: null);
            }
        }

        return new ParsedRateLimitWindow(
            IsValid: true,
            Value: new CodexRateLimitWindow(usedPercent, resetsAt));
    }

    private readonly record struct ParsedRateLimitWindow(
        bool IsValid,
        CodexRateLimitWindow? Value);

    private static long ParseId(JsonElement value)
    {
        if (!value.TryGetInt64(out var id))
        {
            throw new CodexAppServerProtocolException(
                "Codex App Server emitted a non-integer JSON-RPC id.");
        }

        return id;
    }

    private static string RequiredString(
        JsonElement parent,
        string propertyName,
        string context,
        int maximumLength = 1_000_000)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Length > maximumLength)
        {
            throw new CodexAppServerProtocolException(
                $"Codex {context} omitted {propertyName}.");
        }

        return value.GetString()!;
    }

    private static string BoundedString(
        JsonElement parent,
        string propertyName,
        string context,
        int maximumLength)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString()!.Length > maximumLength)
        {
            throw new CodexAppServerProtocolException(
                $"Codex {context} returned an invalid {propertyName}.");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(
        JsonElement parent,
        string propertyName,
        int maximumLength)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String ||
            value.GetString()!.Length > maximumLength)
        {
            return null;
        }

        return value.GetString();
    }

    private static bool RequiredBoolean(
        JsonElement parent,
        string propertyName,
        string context)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new CodexAppServerProtocolException(
                $"Codex {context} omitted {propertyName}.");
        }

        return value.GetBoolean();
    }

    private static void RequireObject(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new CodexAppServerProtocolException(
                $"Codex returned an invalid {context}.");
        }
    }
}

/// <summary>A protocol or schema violation from the Codex child process.</summary>
public sealed class CodexAppServerProtocolException : InvalidOperationException
{
    public CodexAppServerProtocolException(string message)
        : base(message)
    {
    }

    public CodexAppServerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A request rejected by the JSON-RPC app server.</summary>
public sealed class CodexAppServerRequestException : InvalidOperationException
{
    public CodexAppServerRequestException(string method, int code)
        : base($"Codex App Server request '{method}' failed with code {code}.")
    {
        Method = method;
        Code = code;
    }

    public string Method { get; }

    public int Code { get; }
}

/// <summary>The owned app-server process ended while a request was pending.</summary>
public sealed class CodexAppServerClosedException : IOException
{
    public CodexAppServerClosedException()
        : base("Codex App Server closed before completing the request.")
    {
    }
}

/// <summary>A bounded request exceeded the configured response timeout.</summary>
public sealed class CodexAppServerTimeoutException : TimeoutException
{
    public CodexAppServerTimeoutException(string method, TimeSpan timeout)
        : base($"Codex App Server request '{method}' timed out after {timeout}.")
    {
        Method = method;
    }

    public string Method { get; }
}
