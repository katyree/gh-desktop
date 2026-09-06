using WinGit.Core.Codex;
using Xunit;

namespace WinGit.Core.Tests;

public sealed class CodexProtocolTests
{
    [Fact]
    public void InitializeResponseIsMappedToServerInfo()
    {
        var info = CodexAppServerProtocol.ParseInitializeResponse("""
            {
              "codexHome": "C:\\Users\\Test User\\.codex",
              "platformFamily": "windows",
              "platformOs": "windows",
              "userAgent": "codex-cli/0.151.0"
            }
            """);

        Assert.Equal("windows", info.PlatformFamily);
        Assert.Equal("windows", info.PlatformOs);
        Assert.Equal("codex-cli/0.151.0", info.UserAgent);
        Assert.EndsWith(".codex", info.CodexHome, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountResponseExposesMetadataWithoutCredentialFields()
    {
        var state = CodexAppServerProtocol.ParseAccountResponse("""
            {
              "account": {
                "type": "chatgpt",
                "email": "Test User@example.invalid",
                "planType": "plus",
                "accessToken": "must not be retained"
              },
              "requiresOpenaiAuth": false
            }
            """);

        Assert.Equal(CodexAccountStatus.SignedIn, state.Status);
        Assert.Equal(CodexAccountType.ChatGpt, state.Type);
        Assert.Equal("Test User@example.invalid", state.Email);
        Assert.Equal("plus", state.PlanType);
        Assert.False(state.RequiresOpenAiAuth);
    }

    [Fact]
    public void RateLimitResponseCalculatesStatusAndEarliestReset()
    {
        var state = CodexAppServerProtocol.ParseRateLimitsResponse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 82, "resetsAt": 2000 },
                "secondary": { "usedPercent": 35, "resetsAt": 1000 },
                "spendControlReached": false,
                "rateLimitReachedType": null
              }
            }
            """);

        Assert.Equal(CodexRateLimitStatus.NearLimit, state.Status);
        Assert.Equal(82, state.Primary?.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000), state.ResetsAt);
    }

    [Fact]
    public void ModelCatalogFiltersHiddenEntriesAndKeepsVisibleMetadata()
    {
        var page = CodexAppServerProtocol.ParseModelListResponse("""
            {
              "data": [
                {
                  "id": "visible-id",
                  "model": "visible-model",
                  "displayName": "Visible model",
                  "description": "A visible model",
                  "hidden": false,
                  "isDefault": true,
                  "defaultReasoningEffort": "medium",
                  "supportedReasoningEfforts": [
                    { "reasoningEffort": "medium", "description": "Balanced" }
                  ],
                  "apiKey": "must not be retained"
                },
                {
                  "id": "hidden-id",
                  "model": "hidden-model",
                  "displayName": "Hidden model",
                  "description": "Hidden",
                  "hidden": true,
                  "isDefault": false,
                  "defaultReasoningEffort": "low",
                  "supportedReasoningEfforts": []
                }
              ],
              "nextCursor": "next-page"
            }
            """);

        var model = Assert.Single(page.Models);
        Assert.Equal("visible-id", model.Id);
        Assert.Equal("medium", model.DefaultReasoningEffort);
        Assert.Single(model.SupportedReasoningEfforts);
        Assert.Equal("next-page", page.NextCursor);
    }

    [Fact]
    public void LoginStartsMapBrowserAndDeviceCodeWithoutSensitiveDiagnostics()
    {
        var browser = CodexAppServerProtocol.ParseLoginStartResponse(
            """
            {
              "type": "chatgpt",
              "loginId": "opaque-browser-login",
              "authUrl": "https://auth.openai.com/authorize?state=TestState"
            }
            """,
            CodexLoginMethod.Browser);

        Assert.Equal(CodexLoginMethod.Browser, browser.Method);
        Assert.Equal("opaque-browser-login", browser.LoginId);
        Assert.Equal(
            "https://auth.openai.com/authorize?state=TestState",
            browser.AuthorizationUrl);
        Assert.Null(browser.UserCode);
        Assert.DoesNotContain(browser.AuthorizationUrl, browser.ToString());
        Assert.DoesNotContain(browser.LoginId, browser.ToString());

        var device = CodexAppServerProtocol.ParseLoginStartResponse(
            """
            {
              "type": "chatgptDeviceCode",
              "loginId": "opaque-device-login",
              "verificationUrl": "https://chatgpt.com/device",
              "userCode": "TEST-CODE-123"
            }
            """,
            CodexLoginMethod.DeviceCode);

        Assert.Equal(CodexLoginMethod.DeviceCode, device.Method);
        Assert.Equal("https://chatgpt.com/device", device.AuthorizationUrl);
        Assert.Equal("TEST-CODE-123", device.UserCode);
        Assert.DoesNotContain(device.AuthorizationUrl, device.ToString());
        Assert.DoesNotContain(device.UserCode!, device.ToString());
    }

    [Fact]
    public void LoginStartRejectsUntrustedAuthorizationUrlsWithoutEchoingValues()
    {
        var responses = new[]
        {
            """
            {
              "type": "chatgpt",
              "loginId": "opaque-http",
              "authUrl": "http://auth.openai.com/authorize?secret=TestSecret"
            }
            """,
            """
            {
              "type": "chatgpt",
              "loginId": "opaque-host",
              "authUrl": "https://evil.example/authorize?secret=TestSecret"
            }
            """,
            """
            {
              "type": "chatgpt",
              "loginId": "opaque-userinfo",
              "authUrl": "https://user:pass@chatgpt.com/authorize"
            }
            """,
        };

        foreach (var response in responses)
        {
            var exception = Assert.Throws<CodexAppServerProtocolException>(() =>
                CodexAppServerProtocol.ParseLoginStartResponse(
                    response,
                    CodexLoginMethod.Browser));

            Assert.DoesNotContain("TestSecret", exception.ToString());
            Assert.DoesNotContain("evil.example", exception.ToString());
        }
    }

    [Fact]
    public void AccountNotificationsExposeSanitizedLoginCompletion()
    {
        var completed = CodexAppServerProtocol.ParseNotification(
            "account/login/completed",
            """
            {
              "loginId": "opaque-browser-login",
              "success": true,
              "accessToken": "must not be retained"
            }
            """);

        Assert.Equal(CodexNotificationKind.LoginCompleted, completed.Kind);
        Assert.NotNull(completed.LoginCompletion);
        Assert.Equal("opaque-browser-login", completed.LoginCompletion!.LoginId);
        Assert.True(completed.LoginCompletion!.Success);
        Assert.DoesNotContain("accessToken", completed.ToString());
        Assert.DoesNotContain("opaque-browser-login", completed.ToString());

        var accountUpdated = CodexAppServerProtocol.ParseNotification(
            "account/updated",
            """
            {
              "authMode": "chatgpt",
              "planType": "plus",
              "accessToken": "must not be retained"
            }
            """);

        Assert.Equal(CodexNotificationKind.AccountUpdated, accountUpdated.Kind);
        Assert.Equal("chatgpt", accountUpdated.AuthMode);
        Assert.Equal("plus", accountUpdated.PlanType);
        Assert.DoesNotContain("accessToken", accountUpdated.ToString());

        var malformed = CodexAppServerProtocol.ParseNotification(
            "account/login/completed",
            """{ "success": "true" }""");
        Assert.Null(malformed.LoginCompletion);
    }

    [Fact]
    public void GenerationNotificationsExposeProgressAndSanitizedResults()
    {
        var started = CodexAppServerProtocol.ParseNotification(
            "turn/started",
            """
            {
              "threadId": "opaque-thread",
              "turn": { "id": "opaque-turn", "status": "inProgress" }
            }
            """);

        Assert.Equal(CodexNotificationKind.GenerationProgress, started.Kind);
        Assert.Equal(
            CodexGenerationProgressKind.TurnStarted,
            started.GenerationProgress?.Kind);
        Assert.Equal("opaque-turn", started.GenerationProgress?.TurnId);

        var item = CodexAppServerProtocol.ParseNotification(
            "item/completed",
            """
            {
              "threadId": "opaque-thread",
              "turnId": "opaque-turn",
              "item": {
                "id": "opaque-item",
                "type": "agentMessage",
                "text": "private intermediate content"
              }
            }
            """);

        Assert.Equal(
            CodexGenerationProgressKind.ItemCompleted,
            item.GenerationProgress?.Kind);
        Assert.Equal("agentMessage", item.GenerationProgress?.ItemType);
        Assert.DoesNotContain("private intermediate content", item.ToString());

        var completed = CodexAppServerProtocol.ParseNotification(
            "turn/completed",
            """
            {
              "threadId": "opaque-thread",
              "turn": {
                "id": "opaque-turn",
                "status": "completed",
                "items": [
                  {
                    "type": "agentMessage",
                    "phase": "commentary",
                    "text": "draft"
                  },
                  {
                    "type": "agentMessage",
                    "phase": "final_answer",
                    "text": "Generated commit message"
                  }
                ],
                "error": { "message": "private failure detail" }
              },
              "accessToken": "must not be retained"
            }
            """);

        var completion = completed.GenerationProgress?.Completion;
        Assert.Equal(CodexGenerationProgressKind.TurnCompleted, completed.GenerationProgress?.Kind);
        Assert.Equal(CodexGenerationOutcome.Success, completion?.Outcome);
        Assert.Equal("Generated commit message", completion?.Output);
        Assert.DoesNotContain("Generated commit message", completed.ToString());
        Assert.DoesNotContain("private failure detail", completed.ToString());
        Assert.DoesNotContain("accessToken", completed.ToString());

        var interrupted = CodexAppServerProtocol.ParseNotification(
            "turn/completed",
            """
            {
              "threadId": "opaque-thread",
              "turn": {
              "id": "opaque-cancelled", "status": "interrupted", "items": []
              }
            }
            """);
        Assert.Equal(
            CodexGenerationOutcome.Cancelled,
            interrupted.GenerationProgress?.Completion?.Outcome);

        var rateLimited = CodexAppServerProtocol.ParseNotification(
            "turn/completed",
            """
            {
              "threadId": "opaque-thread",
              "turn": {
                "id": "opaque-rate-limited",
                "status": "failed",
                "error": {
                  "codexErrorInfo": "rateLimitExceeded",
                  "message": "private rate-limit detail"
                }
              }
            }
            """);
        Assert.Equal(
            CodexGenerationOutcome.RateLimited,
            rateLimited.GenerationProgress?.Completion?.Outcome);
        Assert.DoesNotContain("private rate-limit detail", rateLimited.ToString());
    }

    [Fact]
    public async Task StopAndDisposeAreSafeBeforeAProcessStarts()
    {
        await using var client = new CodexAppServerClient(
            new CodexAppServerClientOptions
            {
                ExecutablePath = "codex-not-installed-for-test",
            });

        await client.StopAsync();
        Assert.Equal(CodexAppServerState.Stopped, client.State);
    }

    [Fact]
    public async Task PreCancelledStartDoesNotLaunchAProcess()
    {
        var codexHome = Path.Combine(
            Path.GetTempPath(),
            "WinGit-CodexTests",
            "pre-cancelled-" + Guid.NewGuid().ToString("N"));
        await using var client = new CodexAppServerClient(
            new CodexAppServerClientOptions
            {
                ExecutablePath = "codex-not-installed-for-test",
                CodexHomePath = codexHome,
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.StartAsync(cancellation.Token));
            Assert.Equal(CodexAppServerState.Stopped, client.State);
            Assert.Null(client.ProcessId);
            Assert.False(Directory.Exists(codexHome));
        }
        finally
        {
            if (Directory.Exists(codexHome))
            {
                Directory.Delete(codexHome, recursive: true);
            }
        }
    }
}
