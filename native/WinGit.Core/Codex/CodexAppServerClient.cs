using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WinGit.Core.Codex;

/// <summary>
/// A small native client for the local Codex App Server. It owns exactly one
/// child process, keeps JSON-RPC correlation inside this class, and exposes only
/// the account, login, usage, model, generation, and notification data needed
/// by native UI.
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private static readonly string[] CredentialEnvironmentVariables =
    [
        "CHATGPT_ACCESS_TOKEN",
        "CODEX_ACCESS_TOKEN",
        "OPENAI_API_KEY",
        "OPENAI_ORG_ID",
        "OPENAI_PROJECT_ID",
    ];

    private readonly CodexAppServerClientOptions options;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly ConcurrentDictionary<long, PendingRequest> pendingRequests = new();
    private readonly Dictionary<GenerationKey, GenerationState> activeGenerations = new();
    private readonly Dictionary<string, long> activeGenerationThreads =
        new(StringComparer.Ordinal);
    private readonly Dictionary<GenerationKey, CodexGenerationResult> completedGenerations = new();
    private readonly string executablePath;
    private Process? process;
    private StreamWriter? processInput;
    private CancellationTokenSource? processCancellation;
    private Task? standardOutputPump;
    private Task? standardErrorPump;
    private CodexAppServerInfo? serverInfo;
    private CodexAppServerState state = CodexAppServerState.Stopped;
    private long processGeneration;
    private long nextRequestId;
    private bool disposed;

    public CodexAppServerClient(CodexAppServerClientOptions? options = null)
    {
        this.options = options ?? new CodexAppServerClientOptions();
        ValidateOptions(this.options);
        executablePath = CodexExecutableLocator.Resolve(
            this.options.ExecutablePath,
            this.options.ApplicationRoot);
    }

    /// <summary>Raised after a known or unknown server notification is sanitized.</summary>
    public event EventHandler<CodexServerNotification>? NotificationReceived;

    /// <summary>Raised for sanitized lifecycle updates from an active generation.</summary>
    public event EventHandler<CodexGenerationProgress>? GenerationProgressReceived;

    public CodexAppServerState State
    {
        get
        {
            lock (stateGate)
            {
                return state;
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (stateGate)
            {
                return process is { HasExited: false } child ? child.Id : null;
            }
        }
    }

    public bool IsRunning => State == CodexAppServerState.Running && ProcessId is not null;

    /// <summary>Starts and initializes the owned app-server process.</summary>
    public async Task<CodexAppServerInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (serverInfo is not null && IsProcessAlive())
            {
                return serverInfo;
            }

            await StopCoreAsync().ConfigureAwait(false);
            SetState(CodexAppServerState.Starting);

            var child = StartProcess();
            var cancellation = new CancellationTokenSource();
            long generation;
            lock (stateGate)
            {
                process = child;
                generation = ++processGeneration;
                processCancellation = cancellation;
                // Use Process's own writer. Creating a second StreamWriter over the
                // same pipe can close or reorder the first writer during teardown.
                processInput = child.StandardInput;
                processInput.AutoFlush = true;
            }

            child.Exited += HandleProcessExited;
            var outputPump = ReadStandardOutputAsync(
                child.StandardOutput.BaseStream,
                child,
                generation,
                cancellation.Token);
            var errorPump = DrainStandardErrorAsync(
                child.StandardError.BaseStream,
                cancellation.Token);
            lock (stateGate)
            {
                if (IsCurrentProcessLocked(child, generation))
                {
                    standardOutputPump = outputPump;
                    standardErrorPump = errorPump;
                }
            }

            child.EnableRaisingEvents = true;

            var initializeResult = await SendRequestAsync(
                CodexAppServerMethods.Initialize,
                new
                {
                    clientInfo = new
                    {
                        name = options.ClientName,
                        title = options.ClientTitle,
                        version = options.ClientVersion,
                    },
                    capabilities = new
                    {
                        experimentalApi = options.ExperimentalApi,
                    },
                },
                cancellationToken).ConfigureAwait(false);
            var info = CodexAppServerProtocol.ParseInitializeResponse(initializeResult);
            await SendNotificationAsync(
                CodexAppServerMethods.Initialized,
                parameters: null,
                expectedGeneration: generation).ConfigureAwait(false);
            lock (stateGate)
            {
                if (!IsCurrentProcessLocked(child, generation) || child.HasExited)
                {
                    throw new CodexAppServerClosedException();
                }

                serverInfo = info;
                state = CodexAppServerState.Running;
            }
            return info;
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetState(CodexAppServerState.Stopped);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Stops the owned process and completes pending requests.</summary>
    public async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == CodexAppServerState.Stopped && process is null)
            {
                return;
            }

            SetState(CodexAppServerState.Stopping);
            await StopCoreAsync().ConfigureAwait(false);
            SetState(CodexAppServerState.Stopped);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Reads credential-free account metadata from Codex.</summary>
    public async Task<CodexAccountState> ReadAccountAsync(
        bool refreshToken = false,
        CancellationToken cancellationToken = default)
    {
        JsonElement response;
        try
        {
            response = await SendRequestAsync(
                CodexAppServerMethods.ReadAccount,
                new { refreshToken },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CodexAppServerRequestException)
        {
            return CodexAccountState.UnavailableState;
        }

        try
        {
            return CodexAppServerProtocol.ParseAccountResponse(response);
        }
        catch (CodexAppServerProtocolException)
        {
            return CodexAccountState.UnavailableState;
        }
    }

    /// <summary>Reads the legacy single-bucket usage snapshot.</summary>
    public async Task<CodexRateLimitState> ReadRateLimitsAsync(
        CancellationToken cancellationToken = default)
    {
        JsonElement response;
        try
        {
            response = await SendRequestAsync(
                CodexAppServerMethods.ReadRateLimits,
                parameters: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CodexAppServerRequestException)
        {
            return CodexRateLimitState.UnavailableState;
        }

        try
        {
            return CodexAppServerProtocol.ParseRateLimitsResponse(response);
        }
        catch (CodexAppServerProtocolException)
        {
            return CodexRateLimitState.UnavailableState;
        }
    }

    /// <summary>Reads all visible models, following the server cursor.</summary>
    public async Task<IReadOnlyList<CodexModel>> ReadModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var models = new List<CodexModel>();
        var seenModelIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var page = 0; page < 100; page++)
        {
            var response = await SendRequestAsync(
                CodexAppServerMethods.ListModels,
                new
                {
                    cursor,
                    includeHidden = false,
                },
                cancellationToken).ConfigureAwait(false);
            var modelPage = CodexAppServerProtocol.ParseModelListResponse(response);
            foreach (var model in modelPage.Models)
            {
                if (seenModelIds.Add(model.Id))
                {
                    models.Add(model);
                }
            }

            if (modelPage.NextCursor is null)
            {
                return models.AsReadOnly();
            }

            if (!seenCursors.Add(modelPage.NextCursor))
            {
                throw new CodexAppServerProtocolException(
                    "Codex returned a repeated model catalog cursor.");
            }

            cursor = modelPage.NextCursor;
        }

        throw new CodexAppServerProtocolException(
            "Codex returned too many model catalog pages.");
    }

    /// <summary>Starts an explicit browser or device-code account login.</summary>
    public async Task<CodexLoginStart> StartAccountLoginAsync(
        CodexLoginMethod method,
        CancellationToken cancellationToken = default)
    {
        object parameters = method switch
        {
            CodexLoginMethod.Browser => new
            {
                type = "chatgpt",
                codexStreamlinedLogin = true,
                useHostedLoginSuccessPage = false,
            },
            CodexLoginMethod.DeviceCode => new
            {
                type = "chatgptDeviceCode",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        var response = await SendRequestAsync(
            CodexAppServerMethods.LoginStart,
            parameters,
            cancellationToken).ConfigureAwait(false);
        return CodexAppServerProtocol.ParseLoginStartResponse(response, method);
    }

    /// <summary>Cancels one explicit account login using its opaque id.</summary>
    public async Task CancelAccountLoginAsync(
        string loginId,
        CancellationToken cancellationToken = default)
    {
        var validatedLoginId = ValidateLoginId(loginId);
        await SendRequestAsync(
            CodexAppServerMethods.LoginCancel,
            new { loginId = validatedLoginId },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs out and then reads the sanitized account state.</summary>
    public async Task<CodexAccountState> LogoutAccountAsync(
        CancellationToken cancellationToken = default)
    {
        await SendRequestAsync(
            CodexAppServerMethods.Logout,
            parameters: null,
            cancellationToken).ConfigureAwait(false);
        return await ReadAccountAsync(
            refreshToken: false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts one ephemeral read-only turn using the selected model and
    /// reasoning effort, when supplied.
    /// </summary>
    public async Task<CodexGenerationHandle> StartGenerationAsync(
        CodexGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        CodexAppServerProtocol.ValidateGenerationRequest(request);
        var generation = GetCurrentProcessGeneration();
        var generationWorkingDirectory = ResolveGenerationWorkingDirectory();
        EnsureCodexHomeDirectory(generationWorkingDirectory);

        var threadParameters = new Dictionary<string, object?>
        {
            ["cwd"] = generationWorkingDirectory,
            ["runtimeWorkspaceRoots"] = Array.Empty<string>(),
            ["approvalPolicy"] = "never",
            ["sandbox"] = "read-only",
            ["ephemeral"] = true,
            ["environments"] = Array.Empty<string>(),
            ["dynamicTools"] = Array.Empty<string>(),
            ["selectedCapabilityRoots"] = Array.Empty<string>(),
            ["baseInstructions"] = request.Instructions,
            ["config"] = new Dictionary<string, object?>
            {
                ["features"] = new Dictionary<string, bool>
                {
                    ["shell_tool"] = false,
                    ["unified_exec"] = false,
                    ["code_mode_host"] = false,
                    ["multi_agent"] = false,
                    ["apps"] = false,
                    ["plugins"] = false,
                    ["hooks"] = false,
                    ["web_search"] = false,
                    ["tool_suggest"] = false,
                    ["skill_search"] = false,
                    ["browser_use"] = false,
                    ["computer_use"] = false,
                    ["image_generation"] = false,
                },
                ["mcp_servers"] = new Dictionary<string, object?>(),
            },
        };
        if (request.Model is not null)
        {
            threadParameters["model"] = request.Model;
        }

        var threadResponse = await SendRequestAsync(
            CodexAppServerMethods.ThreadStart,
            threadParameters,
            cancellationToken,
            expectedGeneration: generation).ConfigureAwait(false);
        var threadId = CodexAppServerProtocol.ParseThreadStartResponse(threadResponse);
        lock (stateGate)
        {
            if (!IsProcessGenerationRunningLocked(generation))
            {
                throw new CodexAppServerClosedException();
            }

            activeGenerationThreads[threadId] = generation;
        }

        try
        {
            var turnParameters = new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
                ["input"] = new[]
                {
                    new
                    {
                        type = "text",
                        text = request.Prompt,
                        text_elements = Array.Empty<object>(),
                    },
                },
            };
            if (request.ReasoningEffort is not null)
            {
                turnParameters["effort"] = request.ReasoningEffort;
            }

            if (request.OutputSchema is { } outputSchema)
            {
                turnParameters["outputSchema"] = outputSchema.Clone();
            }

            var turnResponse = await SendRequestAsync(
                CodexAppServerMethods.TurnStart,
                turnParameters,
                cancellationToken,
                expectedGeneration: generation).ConfigureAwait(false);
            var turnId = CodexAppServerProtocol.ParseTurnStartResponse(turnResponse);
            var handle = new CodexGenerationHandle(threadId, turnId);
            lock (stateGate)
            {
                if (!IsProcessGenerationRunningLocked(generation))
                {
                    throw new CodexAppServerClosedException();
                }

                var key = new GenerationKey(threadId, turnId);
                if (!completedGenerations.ContainsKey(key))
                {
                    activeGenerations[key] = new GenerationState(generation);
                }

                activeGenerationThreads.Remove(threadId);
            }

            return handle;
        }
        catch
        {
            lock (stateGate)
            {
                if (activeGenerationThreads.TryGetValue(threadId, out var activeGeneration) &&
                    activeGeneration == generation)
                {
                    activeGenerationThreads.Remove(threadId);
                }
            }

            throw;
        }
    }

    /// <summary>Waits for one turn, interrupting it after the configured bound.</summary>
    public async Task<CodexGenerationResult> WaitForGenerationAsync(
        CodexGenerationHandle handle,
        CancellationToken cancellationToken = default)
    {
        var validatedHandle = ValidateGenerationHandle(handle);
        var key = new GenerationKey(validatedHandle.ThreadId, validatedHandle.TurnId);
        Task<CodexGenerationResult> completion;
        long generation;
        lock (stateGate)
        {
            if (completedGenerations.TryGetValue(key, out var completed))
            {
                return completed;
            }

            if (!activeGenerations.TryGetValue(key, out var active))
            {
                throw new InvalidOperationException("Unknown Codex generation.");
            }

            completion = active.Completion.Task;
            generation = active.ProcessGeneration;
        }

        try
        {
            return await completion
                .WaitAsync(options.GenerationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var timeout = new CodexGenerationResult(
                CodexGenerationOutcome.Timeout,
                Output: null);
            if (CompleteGeneration(validatedHandle, timeout, generation))
            {
                await InterruptGenerationAsync(validatedHandle, generation)
                    .ConfigureAwait(false);
                return timeout;
            }

            return GetCompletedGeneration(validatedHandle) ?? timeout;
        }
    }

    /// <summary>Interrupts one active turn and completes it as cancelled.</summary>
    public async Task CancelGenerationAsync(
        CodexGenerationHandle handle,
        CancellationToken cancellationToken = default)
    {
        var validatedHandle = ValidateGenerationHandle(handle);
        var key = new GenerationKey(validatedHandle.ThreadId, validatedHandle.TurnId);
        long generation;
        lock (stateGate)
        {
            if (completedGenerations.ContainsKey(key))
            {
                return;
            }

            if (!activeGenerations.TryGetValue(key, out var active))
            {
                throw new InvalidOperationException("Unknown Codex generation.");
            }

            generation = active.ProcessGeneration;
        }

        try
        {
            await SendRequestAsync(
                CodexAppServerMethods.TurnInterrupt,
                new
                {
                    threadId = validatedHandle.ThreadId,
                    turnId = validatedHandle.TurnId,
                },
                cancellationToken,
                expectedGeneration: generation).ConfigureAwait(false);
        }
        finally
        {
            CompleteGeneration(
                validatedHandle,
                new CodexGenerationResult(
                    CodexGenerationOutcome.Cancelled,
                    Output: null),
                generation);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            // Set the flag while holding the lifecycle gate so a concurrent
            // StartAsync cannot pass validation after disposal begins.
            disposed = true;
            SetState(CodexAppServerState.Stopping);
            await StopCoreAsync().ConfigureAwait(false);
            SetState(CodexAppServerState.Stopped);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private Process StartProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = options.WorkingDirectory ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["RUST_LOG"] = "error";
        foreach (var environmentVariable in CredentialEnvironmentVariables)
        {
            startInfo.Environment.Remove(environmentVariable);
        }

        var codexHome = ResolveCodexHomePath();
        if (codexHome is not null)
        {
            EnsureCodexHomeDirectory(codexHome);
            startInfo.Environment["CODEX_HOME"] = codexHome;
        }

        var child = new Process { StartInfo = startInfo };
        try
        {
            if (!child.Start())
            {
                throw new InvalidOperationException(
                    "Unable to start the Codex App Server process.");
            }

            return child;
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken,
        long? expectedGeneration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long requestId;
        long generation;
        PendingRequest pending;
        lock (stateGate)
        {
            if (processInput is null ||
                process is not { HasExited: false } ||
                (expectedGeneration is not null &&
                 processGeneration != expectedGeneration.Value))
            {
                throw new CodexAppServerClosedException();
            }

            requestId = Interlocked.Increment(ref nextRequestId);
            if (requestId <= 0)
            {
                Interlocked.Exchange(ref nextRequestId, 1);
                requestId = 1;
            }

            generation = processGeneration;
            // Register the request under the same gate teardown uses to detach
            // a process and drain its generation.
            pending = new PendingRequest(method, cancellationToken, generation);
            if (!pendingRequests.TryAdd(requestId, pending))
            {
                throw new InvalidOperationException("Codex request id allocation collided.");
            }
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var request = (RequestCancellationState)state!;
                request.Client.CancelPendingRequest(request.RequestId, request.Pending);
            },
            new RequestCancellationState(this, requestId, pending));

        try
        {
            if (!await TryWriteLineAsync(
                    CodexAppServerProtocol.SerializeRequest(requestId, method, parameters),
                    cancellationToken,
                    generation).ConfigureAwait(false))
            {
                CancelPendingRequest(requestId, pending, new CodexAppServerClosedException());
            }

            try
            {
                return await pending.Completion.Task
                    .WaitAsync(options.RequestTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var timeout = new CodexAppServerTimeoutException(
                    method,
                    options.RequestTimeout);
                CancelPendingRequest(requestId, pending, timeout);
                throw timeout;
            }
        }
        finally
        {
            pendingRequests.TryRemove(new KeyValuePair<long, PendingRequest>(requestId, pending));
        }
    }

    private async Task SendNotificationAsync(
        string method,
        object? parameters,
        long? expectedGeneration = null)
    {
        if (!await TryWriteLineAsync(
                CodexAppServerProtocol.SerializeNotification(method, parameters),
                CancellationToken.None,
                expectedGeneration).ConfigureAwait(false))
        {
            throw new CodexAppServerClosedException();
        }
    }

    private async Task<bool> TryWriteLineAsync(
        string line,
        CancellationToken cancellationToken,
        long? expectedGeneration = null)
    {
        StreamWriter? input;
        CancellationToken processToken;
        long generation;
        lock (stateGate)
        {
            input = processInput;
            generation = processGeneration;
            processToken = processCancellation?.Token ?? CancellationToken.None;
            if (input is null || process is not { HasExited: false } ||
                (expectedGeneration is not null &&
                 generation != expectedGeneration.Value))
            {
                return false;
            }
        }

        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            processToken);
        writeCancellation.CancelAfter(options.WriteTimeout);

        var lockTaken = false;
        try
        {
            await writeGate.WaitAsync(writeCancellation.Token).ConfigureAwait(false);
            lockTaken = true;
            lock (stateGate)
            {
                if (!ReferenceEquals(input, processInput) ||
                    processGeneration != generation ||
                    process is not { HasExited: false } ||
                    (expectedGeneration is not null &&
                     processGeneration != expectedGeneration.Value))
                {
                    return false;
                }
            }

            await input!.WriteLineAsync(line.AsMemory(), writeCancellation.Token)
                .ConfigureAwait(false);
            await input.FlushAsync(writeCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (lockTaken)
            {
                writeGate.Release();
            }
        }
    }

    private async Task ReadStandardOutputAsync(
        Stream output,
        Process child,
        long generation,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var line = new MemoryStream(capacity: Math.Min(options.MaximumLineBytes, 64 * 1024));
        try
        {
            while (true)
            {
                var count = await output.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                for (var index = 0; index < count; index++)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        if (line.Length > 0)
                        {
                            if (line.GetBuffer()[(int)line.Length - 1] == (byte)'\r')
                            {
                                line.SetLength(line.Length - 1);
                            }

                            if (line.Length > 0)
                            {
                                HandleLine(Encoding.UTF8.GetString(
                                     line.GetBuffer(),
                                     0,
                                     (int)line.Length),
                                    child,
                                    generation);
                            }
                            line.SetLength(0);
                        }

                        continue;
                    }

                    if (line.Length >= options.MaximumLineBytes)
                    {
                        var exception = new CodexAppServerProtocolException(
                            "Codex App Server emitted an oversized JSON-RPC line.");
                        FailCurrentProcessRequests(child, generation, exception);
                        TryKill(child);
                        return;
                    }

                    line.WriteByte(value);
                }
            }

            if (line.Length > 0)
            {
                if (line.GetBuffer()[(int)line.Length - 1] == (byte)'\r')
                {
                    line.SetLength(line.Length - 1);
                }

                if (line.Length > 0)
                {
                    HandleLine(
                        Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length),
                        child,
                        generation);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            FailCurrentProcessRequests(child, generation);
        }
        catch (ObjectDisposedException)
        {
            FailCurrentProcessRequests(child, generation);
        }
        finally
        {
            FailCurrentProcessRequests(child, generation);
        }
    }

    private static async Task DrainStandardErrorAsync(
        Stream error,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (await error.ReadAsync(buffer.AsMemory(), cancellationToken)
                       .ConfigureAwait(false) > 0)
            {
                // Drain stderr concurrently. Diagnostics are intentionally not
                // retained because Codex may echo credential-bearing text.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void HandleLine(string line, Process child, long generation)
    {
        if (!IsCurrentProcess(child, generation))
        {
            return;
        }

        CodexRpcMessage message;
        try
        {
            message = CodexAppServerProtocol.ParseRpcMessage(line);
        }
        catch (CodexAppServerProtocolException exception)
        {
            FailOldestPendingRequest(exception, generation);
            return;
        }

        if (message.Kind == CodexRpcMessageKind.Response)
        {
            if (message.Id is null ||
                !pendingRequests.TryGetValue(message.Id.Value, out var pending) ||
                pending.Generation != generation ||
                !pendingRequests.TryRemove(
                    new KeyValuePair<long, PendingRequest>(message.Id.Value, pending)))
            {
                return;
            }

            if (message.Error is not null)
            {
                pending.Completion.TrySetException(
                    new CodexAppServerRequestException(pending.Method, message.Error.Code));
            }
            else if (message.Result is JsonElement result)
            {
                pending.Completion.TrySetResult(result);
            }
            else
            {
                pending.Completion.TrySetException(
                    new CodexAppServerProtocolException(
                        $"Codex request '{pending.Method}' returned no result."));
            }

            return;
        }

        if (message.Kind == CodexRpcMessageKind.ServerRequest)
        {
            if (message.Id is not null)
            {
                _ = TryWriteLineAsync(
                    JsonSerializer.Serialize(new
                    {
                        id = message.Id.Value,
                        error = new
                        {
                            code = -32601,
                            message = "WinGit does not support server requests in the read-only bridge.",
                        },
                    }),
                    CancellationToken.None,
                    generation);
            }

            return;
        }

        if (message.Method.Length == 0)
        {
            return;
        }

        var notification = CodexAppServerProtocol.ParseNotification(
            message.Method,
            message.Parameters);

        if (notification.GenerationProgress is { } generationProgress)
        {
            if (generationProgress.Kind == CodexGenerationProgressKind.TurnCompleted &&
                generationProgress.Completion is { } completion &&
                generationProgress.ThreadId is { } threadId &&
                generationProgress.TurnId is { } turnId)
            {
                CompleteGeneration(
                    new CodexGenerationHandle(threadId, turnId),
                    completion,
                    generation);
            }

            try
            {
                GenerationProgressReceived?.Invoke(this, generationProgress);
            }
            catch
            {
                // UI listeners cannot interrupt protocol processing.
            }
        }

        try
        {
            NotificationReceived?.Invoke(this, notification);
        }
        catch
        {
            // UI listeners cannot interrupt protocol processing.
        }
    }

    private void HandleProcessExited(object? sender, EventArgs args)
    {
        if (sender is not Process child ||
            !TryMarkCurrentProcessStopped(
                child,
                out var pending,
                out var generations))
        {
            return;
        }

        CompletePendingRequests(pending, new CodexAppServerClosedException());
        CompleteGenerations(
            generations,
            new CodexGenerationResult(
                CodexGenerationOutcome.RuntimeError,
                Output: null));
    }

    private void CancelPendingRequest(
        long requestId,
        PendingRequest pending,
        Exception? exception = null)
    {
        if (!pendingRequests.TryRemove(new KeyValuePair<long, PendingRequest>(requestId, pending)))
        {
            return;
        }

        _ = TryWriteLineAsync(
            CodexAppServerProtocol.SerializeNotification(
                CodexAppServerMethods.CancelRequest,
                new { id = requestId }),
            CancellationToken.None,
            pending.Generation);
        if (exception is not null)
        {
            pending.Completion.TrySetException(exception);
        }
        else
        {
            pending.Completion.TrySetCanceled(pending.CancellationToken);
        }
    }

    private void FailOldestPendingRequest(Exception exception, long generation)
    {
        PendingRequest? pending = null;
        lock (stateGate)
        {
            foreach (var pair in pendingRequests.OrderBy(pair => pair.Key))
            {
                if (pair.Value.Generation != generation ||
                    !pendingRequests.TryRemove(pair.Key, out pending))
                {
                    continue;
                }

                break;
            }
        }

        if (pending is not null)
        {
            pending.Completion.TrySetException(exception);
        }
    }

    private async Task StopCoreAsync()
    {
        Process? child;
        StreamWriter? input;
        CancellationTokenSource? cancellation;
        Task? outputPump;
        Task? errorPump;
        List<PendingRequest> pending;
        List<GenerationCompletion> generations;
        lock (stateGate)
        {
            child = process;
            if (child is not null)
            {
                child.Exited -= HandleProcessExited;
            }

            process = null;
            input = processInput;
            processInput = null;
            cancellation = processCancellation;
            processCancellation = null;
            outputPump = standardOutputPump;
            standardOutputPump = null;
            errorPump = standardErrorPump;
            standardErrorPump = null;
            serverInfo = null;
            pending = TakePendingRequestsLocked();
            generations = TakeActiveGenerationsLocked();
        }

        CompletePendingRequests(pending, new CodexAppServerClosedException());
        CompleteGenerations(
            generations,
            new CodexGenerationResult(
                CodexGenerationOutcome.RuntimeError,
                Output: null));
        cancellation?.Cancel();
        var inputGateTaken = false;
        inputGateTaken = await writeGate
            .WaitAsync(options.WriteTimeout)
            .ConfigureAwait(false);

        try
        {
            if (inputGateTaken)
            {
                input?.Dispose();
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (inputGateTaken)
            {
                writeGate.Release();
            }
        }

        if (child is not null)
        {
            try
            {
                if (!child.HasExited)
                {
                    await child.WaitForExitAsync()
                        .WaitAsync(options.ShutdownTimeout)
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                TryKill(child);
                try
                {
                    await child.WaitForExitAsync()
                        .WaitAsync(options.ShutdownTimeout)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        var pumps = new[] { outputPump, errorPump }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (pumps.Length > 0)
        {
            try
            {
                await Task.WhenAll(pumps)
                    .WaitAsync(options.ShutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }

        cancellation?.Dispose();
        if (!inputGateTaken)
        {
            try
            {
                input?.Dispose();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
        child?.Dispose();
    }

    private long GetCurrentProcessGeneration()
    {
        lock (stateGate)
        {
            if (!IsProcessGenerationRunningLocked(processGeneration))
            {
                throw new CodexAppServerClosedException();
            }

            return processGeneration;
        }
    }

    private bool IsProcessGenerationRunningLocked(long generation)
    {
        return state == CodexAppServerState.Running &&
            serverInfo is not null &&
            processGeneration == generation &&
            processInput is not null &&
            process is { HasExited: false };
    }

    private bool IsProcessAlive()
    {
        lock (stateGate)
        {
            return process is { HasExited: false };
        }
    }

    private bool IsCurrentProcess(Process child, long generation)
    {
        lock (stateGate)
        {
            return IsCurrentProcessLocked(child, generation);
        }
    }

    private bool IsCurrentProcessLocked(Process child, long generation)
    {
        return ReferenceEquals(process, child) && processGeneration == generation;
    }

    private void FailCurrentProcessRequests(
        Process child,
        long generation,
        Exception? exception = null)
    {
        List<PendingRequest>? pending = null;
        List<GenerationCompletion>? generations = null;
        lock (stateGate)
        {
            if (IsCurrentProcessLocked(child, generation))
            {
                pending = TakePendingRequestsLocked(generation);
                generations = TakeActiveGenerationsLocked();
            }
        }

        if (pending is not null)
        {
            CompletePendingRequests(
                pending,
                exception ?? new CodexAppServerClosedException());
        }

        if (generations is not null)
        {
            CompleteGenerations(
                generations,
                new CodexGenerationResult(
                    CodexGenerationOutcome.RuntimeError,
                    Output: null));
        }
    }

    private bool TryMarkCurrentProcessStopped(
        Process child,
        out List<PendingRequest> pending,
        out List<GenerationCompletion> generations)
    {
        lock (stateGate)
        {
            pending = new List<PendingRequest>();
            generations = new List<GenerationCompletion>();
            if (!ReferenceEquals(process, child))
            {
                return false;
            }

            serverInfo = null;
            state = CodexAppServerState.Stopped;
            pending = TakePendingRequestsLocked(processGeneration);
            generations = TakeActiveGenerationsLocked();
            return true;
        }
    }

    private List<PendingRequest> TakePendingRequestsLocked(long? generation = null)
    {
        // Callers hold stateGate so a new generation cannot register while a
        // previous generation is being detached and drained.
        var pending = new List<PendingRequest>();
        foreach (var pair in pendingRequests)
        {
            if (generation is not null && pair.Value.Generation != generation.Value)
            {
                continue;
            }

            if (pendingRequests.TryRemove(pair.Key, out var removed))
            {
                pending.Add(removed);
            }
        }

        return pending;
    }

    private List<GenerationCompletion> TakeActiveGenerationsLocked()
    {
        var result = new CodexGenerationResult(
            CodexGenerationOutcome.RuntimeError,
            Output: null);
        var completions = new List<GenerationCompletion>();
        foreach (var pair in activeGenerations)
        {
            completedGenerations[pair.Key] = result;
            completions.Add(new GenerationCompletion(pair.Value.Completion));
        }

        activeGenerations.Clear();
        activeGenerationThreads.Clear();
        TrimCompletedGenerationsLocked();
        return completions;
    }

    private bool CompleteGeneration(
        CodexGenerationHandle handle,
        CodexGenerationResult result,
        long expectedGeneration)
    {
        var key = new GenerationKey(handle.ThreadId, handle.TurnId);
        TaskCompletionSource<CodexGenerationResult>? completion = null;
        lock (stateGate)
        {
            if (completedGenerations.ContainsKey(key))
            {
                return false;
            }

            if (activeGenerations.Remove(key, out var active))
            {
                if (active.ProcessGeneration != expectedGeneration)
                {
                    activeGenerations[key] = active;
                    return false;
                }

                completion = active.Completion;
                activeGenerationThreads.Remove(handle.ThreadId);
            }
            else if (activeGenerationThreads.TryGetValue(
                         handle.ThreadId,
                         out var generation) &&
                     generation == expectedGeneration)
            {
                activeGenerationThreads.Remove(handle.ThreadId);
            }
            else
            {
                return false;
            }

            completedGenerations[key] = result;
            TrimCompletedGenerationsLocked();
        }

        completion?.TrySetResult(result);
        return true;
    }

    private CodexGenerationResult? GetCompletedGeneration(
        CodexGenerationHandle handle)
    {
        lock (stateGate)
        {
            return completedGenerations.TryGetValue(
                new GenerationKey(handle.ThreadId, handle.TurnId),
                out var result)
                ? result
                : null;
        }
    }

    private async Task InterruptGenerationAsync(
        CodexGenerationHandle handle,
        long expectedGeneration)
    {
        try
        {
            await SendRequestAsync(
                CodexAppServerMethods.TurnInterrupt,
                new
                {
                    threadId = handle.ThreadId,
                    turnId = handle.TurnId,
                },
                CancellationToken.None,
                expectedGeneration).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            IOException or
            TimeoutException)
        {
            // The terminal timeout result remains useful when the process is
            // already closing or the interrupt itself cannot be delivered.
        }
    }

    private void CompleteGenerations(
        IEnumerable<GenerationCompletion> generations,
        CodexGenerationResult result)
    {
        foreach (var generation in generations)
        {
            generation.Completion.TrySetResult(result);
        }
    }

    private static CodexGenerationHandle ValidateGenerationHandle(
        CodexGenerationHandle? handle)
    {
        if (handle is null)
        {
            throw new ArgumentNullException(nameof(handle));
        }

        return new CodexGenerationHandle(
            ValidateOpaqueIdentifier(handle.ThreadId, nameof(handle.ThreadId)),
            ValidateOpaqueIdentifier(handle.TurnId, nameof(handle.TurnId)));
    }

    private static string ValidateOpaqueIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            throw new ArgumentException(
                $"{name} must be a non-empty value of at most 200 characters.",
                name);
        }

        return value;
    }

    private void TrimCompletedGenerationsLocked()
    {
        while (completedGenerations.Count > 100)
        {
            var oldest = completedGenerations.Keys.FirstOrDefault();
            if (oldest == default)
            {
                return;
            }

            completedGenerations.Remove(oldest);
        }
    }

    private static void CompletePendingRequests(
        IEnumerable<PendingRequest> pending,
        Exception exception)
    {
        foreach (var request in pending)
        {
            request.Completion.TrySetException(exception);
        }
    }

    private static void TryKill(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private void SetState(CodexAppServerState newState)
    {
        lock (stateGate)
        {
            state = newState;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static void ValidateOptions(CodexAppServerClientOptions values)
    {
        if (string.IsNullOrWhiteSpace(values.ClientName) ||
            string.IsNullOrWhiteSpace(values.ClientTitle) ||
            string.IsNullOrWhiteSpace(values.ClientVersion))
        {
            throw new ArgumentException("Codex client identity values are required.");
        }

        if (values.Arguments is null || values.Arguments.Count == 0 ||
            values.Arguments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Codex app-server arguments are required.");
        }

        if (values.WorkingDirectory is not null &&
            !Path.IsPathFullyQualified(values.WorkingDirectory))
        {
            throw new ArgumentException(
                "Codex working directory must be absolute.",
                nameof(values.WorkingDirectory));
        }

        if (values.GenerationWorkingDirectory is not null &&
            !Path.IsPathFullyQualified(values.GenerationWorkingDirectory))
        {
            throw new ArgumentException(
                "Codex generation working directory must be absolute.",
                nameof(values.GenerationWorkingDirectory));
        }

        if (values.ApplicationRoot is not null &&
            !Path.IsPathFullyQualified(values.ApplicationRoot))
        {
            throw new ArgumentException(
                "Codex application root must be absolute.",
                nameof(values.ApplicationRoot));
        }

        if (values.CodexHomePath is not null &&
            !Path.IsPathFullyQualified(values.CodexHomePath))
        {
            throw new ArgumentException(
                "Codex home must be absolute.",
                nameof(values.CodexHomePath));
        }

        if (values.RequestTimeout <= TimeSpan.Zero ||
            values.GenerationTimeout <= TimeSpan.Zero ||
            values.WriteTimeout <= TimeSpan.Zero ||
            values.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values.RequestTimeout),
                "Codex timeouts must be positive.");
        }

        if (values.MaximumLineBytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values.MaximumLineBytes),
                "Codex maximum line size must be positive.");
        }
    }

    private string? ResolveCodexHomePath()
    {
        if (!string.IsNullOrWhiteSpace(options.CodexHomePath))
        {
            return options.CodexHomePath;
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The native LocalAppData path is unavailable.");
        }

        return Path.Combine(localAppData, "WinGit.Native", "codex");
    }

    private string ResolveGenerationWorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(options.GenerationWorkingDirectory))
        {
            return options.GenerationWorkingDirectory;
        }

        var codexHome = ResolveCodexHomePath();
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            throw new InvalidOperationException(
                "The native Codex profile path is unavailable.");
        }

        return Path.Combine(codexHome, "empty-workspace");
    }

    private static void EnsureCodexHomeDirectory(string codexHome)
    {
        try
        {
            Directory.CreateDirectory(codexHome);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Unable to create the native Codex profile directory.",
                exception);
        }
    }

    private static string ValidateLoginId(string loginId)
    {
        if (string.IsNullOrWhiteSpace(loginId) || loginId.Length > 200)
        {
            throw new ArgumentException(
                "loginId must be a non-empty value of at most 200 characters.",
                nameof(loginId));
        }

        return loginId;
    }

    private readonly record struct GenerationKey(string ThreadId, string TurnId);

    private sealed class GenerationState
    {
        public GenerationState(long processGeneration)
        {
            ProcessGeneration = processGeneration;
        }

        public long ProcessGeneration { get; }

        public TaskCompletionSource<CodexGenerationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record GenerationCompletion(
        TaskCompletionSource<CodexGenerationResult> Completion);

    private sealed class PendingRequest
    {
        public PendingRequest(
            string method,
            CancellationToken cancellationToken,
            long generation)
        {
            Method = method;
            CancellationToken = cancellationToken;
            Generation = generation;
        }

        public string Method { get; }

        public CancellationToken CancellationToken { get; }

        public long Generation { get; }

        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RequestCancellationState(
        CodexAppServerClient Client,
        long RequestId,
        PendingRequest Pending);
}
