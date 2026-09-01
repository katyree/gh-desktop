import { isAbsolute } from 'path'
import {
  CodexLoginMethod,
  CodexJSONValue,
  ICodexAccountState,
  ICodexGenerationCancellation,
  ICodexGenerationHandle,
  ICodexGenerationRequest,
  ICodexGenerationResult,
  ICodexRateLimitState,
} from '../lib/codex-ipc'
import {
  CodexAppServerProcess,
  redactCodexDiagnostic,
} from './codex-app-server-supervisor'
import {
  CodexJSONRPCTransport,
  ICodexServerNotification,
} from './codex-json-rpc'
import {
  sanitizeCodexRateLimits,
  unavailableCodexRateLimits,
} from './codex-rate-limits'

interface ICodexConnection {
  readonly process: CodexAppServerProcess
  readonly transport: CodexJSONRPCTransport
}

type CodexClientLog = (
  level: 'info' | 'warn' | 'error',
  message: string
) => void | Promise<void>

type CodexNotificationListener = (
  notification: ICodexServerNotification
) => void

type CodexAccountStateListener = (state: ICodexAccountState) => void
type CodexRateLimitStateListener = (state: ICodexRateLimitState) => void

interface ICodexGenerationWaiter {
  readonly resolve: (result: ICodexGenerationResult) => void
  readonly timeout: NodeJS.Timeout
}

export interface ICodexManagedLoginStart {
  readonly method: CodexLoginMethod
  readonly loginId: string
  readonly authorizationUrl: string
  readonly userCode?: string
}

export interface ICodexAppServerLifecycle {
  start(): Promise<CodexAppServerProcess>
  shutdown(): Promise<void>
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function requiredString(
  value: unknown,
  field: string,
  maximumLength = 1_000_000
) {
  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`${field} must be a non-empty string`)
  }
  if (value.length > maximumLength) {
    throw new Error(`${field} exceeds ${maximumLength} characters`)
  }
  return value
}

function validateJSONValue(value: CodexJSONValue, depth = 0): void {
  if (depth > 50) {
    throw new Error('outputSchema exceeds the maximum nesting depth')
  }
  if (
    value === null ||
    typeof value === 'string' ||
    typeof value === 'boolean'
  ) {
    return
  }
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) {
      throw new Error('outputSchema contains a non-finite number')
    }
    return
  }
  if (Array.isArray(value)) {
    for (const entry of value) {
      validateJSONValue(entry, depth + 1)
    }
    return
  }
  if (typeof value === 'object') {
    for (const entry of Object.values(value)) {
      validateJSONValue(entry, depth + 1)
    }
    return
  }
  throw new Error('outputSchema contains a non-JSON value')
}

function getGenerationId(
  response: CodexJSONValue | undefined,
  container: 'thread' | 'turn'
) {
  if (!isRecord(response)) {
    throw new Error(`Codex returned an invalid ${container} response`)
  }
  const value = response[container]
  if (!isRecord(value) || typeof value.id !== 'string') {
    throw new Error(`Codex omitted the ${container} id`)
  }
  return value.id
}

function sanitizeAccountResponse(
  response: CodexJSONValue | undefined
): ICodexAccountState {
  if (!isRecord(response) || typeof response.requiresOpenaiAuth !== 'boolean') {
    throw new Error('Codex returned invalid account state')
  }

  const account = response.account
  if (account === null) {
    return {
      status: 'signed-out',
      type: 'signed-out',
      email: null,
      planType: null,
      requiresOpenaiAuth: response.requiresOpenaiAuth,
    }
  }
  if (!isRecord(account) || typeof account.type !== 'string') {
    throw new Error('Codex returned an invalid account')
  }

  switch (account.type) {
    case 'chatgpt':
      return {
        status: response.requiresOpenaiAuth ? 'expired' : 'signed-in',
        type: 'chatgpt',
        email: typeof account.email === 'string' ? account.email : null,
        planType:
          typeof account.planType === 'string' ? account.planType : null,
        requiresOpenaiAuth: response.requiresOpenaiAuth,
      }
    case 'apiKey':
      return {
        status: response.requiresOpenaiAuth ? 'expired' : 'signed-in',
        type: 'api-key',
        email: null,
        planType: null,
        requiresOpenaiAuth: response.requiresOpenaiAuth,
      }
    default:
      return {
        status: response.requiresOpenaiAuth ? 'expired' : 'signed-in',
        type: 'other',
        email: null,
        planType: null,
        requiresOpenaiAuth: response.requiresOpenaiAuth,
      }
  }
}

function accountErrorState(error: unknown): ICodexAccountState {
  const message = error instanceof Error ? error.message.toLowerCase() : ''
  const status = /rate.?limit|too many requests|\b429\b|quota/.test(message)
    ? 'rate-limited'
    : /expired|unauthorized|authentication|\b401\b/.test(message)
    ? 'expired'
    : 'error'

  return {
    status,
    type: 'signed-out',
    email: null,
    planType: null,
    requiresOpenaiAuth: true,
    errorMessage:
      status === 'rate-limited'
        ? 'ChatGPT is temporarily rate limited. Try again after it resets.'
        : status === 'expired'
        ? 'Your ChatGPT session expired. Sign in again.'
        : 'WinGit could not read your ChatGPT account. Try again.',
  }
}

/**
 * Typed main-process facade over App Server. Only sanitized account fields and
 * opaque generation IDs can cross the renderer boundary.
 */
export class CodexAppServerClient {
  private readonly notificationListeners = new Set<CodexNotificationListener>()
  private readonly accountStateListeners = new Set<CodexAccountStateListener>()
  private readonly rateLimitStateListeners =
    new Set<CodexRateLimitStateListener>()
  private connection: ICodexConnection | undefined
  private connecting: Promise<ICodexConnection> | undefined
  private readonly activeGenerationKeys = new Set<string>()
  private readonly activeGenerationThreads = new Set<string>()
  private readonly generationStartedAt = new Map<string, number>()
  private readonly generationResults = new Map<string, ICodexGenerationResult>()
  private readonly generationWaiters = new Map<string, ICodexGenerationWaiter>()

  public constructor(
    private readonly supervisor: ICodexAppServerLifecycle,
    private readonly clientVersion: string,
    private readonly generationWorkingDirectory: string,
    private readonly log: CodexClientLog = () => undefined,
    private readonly generationTimeoutMs = 90_000
  ) {
    if (!isAbsolute(generationWorkingDirectory)) {
      throw new Error('generationWorkingDirectory must be absolute')
    }
    if (!Number.isFinite(generationTimeoutMs) || generationTimeoutMs < 1) {
      throw new Error('generationTimeoutMs must be a positive number')
    }
  }

  public async readAccount(refreshToken = false): Promise<ICodexAccountState> {
    if (typeof refreshToken !== 'boolean') {
      throw new Error('refreshToken must be a boolean')
    }
    try {
      const { transport } = await this.getConnection()
      const response = await transport.request('account/read', { refreshToken })
      return sanitizeAccountResponse(response)
    } catch (error) {
      const state = accountErrorState(error)
      this.emitLog('warn', `Codex account read failed (${state.status})`)
      return state
    }
  }

  public async startAccountLogin(
    method: CodexLoginMethod
  ): Promise<ICodexManagedLoginStart> {
    if (method !== 'browser' && method !== 'device-code') {
      throw new Error('Unsupported Codex login method')
    }

    try {
      const { transport } = await this.getConnection()
      const response = await transport.request(
        'account/login/start',
        method === 'browser'
          ? {
              type: 'chatgpt',
              codexStreamlinedLogin: true,
              useHostedLoginSuccessPage: true,
              appBrand: 'codex',
            }
          : { type: 'chatgptDeviceCode' }
      )
      return this.parseLoginStart(method, response)
    } catch (error) {
      this.emitLog('warn', `Codex ${method} login could not start`)
      throw new Error('WinGit could not start ChatGPT sign-in. Try again.')
    }
  }

  public async readRateLimits(): Promise<ICodexRateLimitState> {
    try {
      const { transport } = await this.getConnection()
      const response = await transport.request('account/rateLimits/read')
      return sanitizeCodexRateLimits(response)
    } catch {
      this.emitLog('warn', 'Codex rate limits are unavailable')
      return unavailableCodexRateLimits
    }
  }

  public async cancelAccountLogin(loginId: string): Promise<void> {
    const validatedLoginId = requiredString(loginId, 'loginId', 200)
    try {
      const { transport } = await this.getConnection()
      await transport.request('account/login/cancel', {
        loginId: validatedLoginId,
      })
    } catch {
      this.emitLog('warn', `Codex account login could not be cancelled`)
      throw new Error('WinGit could not cancel ChatGPT sign-in. Try again.')
    }
  }

  public async logoutAccount(): Promise<ICodexAccountState> {
    try {
      const { transport } = await this.getConnection()
      await transport.request('account/logout')
    } catch {
      this.emitLog('warn', `Codex account logout failed`)
      throw new Error('WinGit could not sign out of ChatGPT. Try again.')
    }

    const state = await this.readAccount(false)
    this.emitAccountState(state)
    return state
  }

  public async startGeneration(
    request: ICodexGenerationRequest
  ): Promise<ICodexGenerationHandle> {
    this.validateGenerationRequest(request)
    const { transport } = await this.getConnection()

    const threadParams: Record<string, CodexJSONValue> = {
      cwd: this.generationWorkingDirectory,
      runtimeWorkspaceRoots: [],
      approvalPolicy: 'never',
      sandbox: 'read-only',
      ephemeral: true,
      environments: [],
      dynamicTools: [],
      selectedCapabilityRoots: [],
      baseInstructions: request.instructions,
      config: {
        features: {
          shell_tool: false,
          unified_exec: false,
          code_mode_host: false,
          multi_agent: false,
          apps: false,
          plugins: false,
          hooks: false,
          web_search: false,
          tool_suggest: false,
          skill_search: false,
          browser_use: false,
          computer_use: false,
          image_generation: false,
        },
        tools: { web_search: null },
        mcp_servers: {},
      },
    }
    if (request.model !== undefined) {
      threadParams.model = request.model
    }

    const threadResponse = await transport.request('thread/start', threadParams)
    const threadId = getGenerationId(threadResponse, 'thread')
    this.activeGenerationThreads.add(threadId)
    this.generationStartedAt.set(threadId, Date.now())
    const turnParams: Record<string, CodexJSONValue> = {
      threadId,
      input: [
        {
          type: 'text',
          text: request.prompt,
          text_elements: [],
        },
      ],
    }
    if (request.outputSchema !== undefined) {
      turnParams.outputSchema = request.outputSchema
    }

    try {
      const turnResponse = await transport.request('turn/start', turnParams)
      const handle = {
        threadId,
        turnId: getGenerationId(turnResponse, 'turn'),
      }
      const key = this.generationKey(handle)
      if (!this.generationResults.has(key)) {
        this.activeGenerationKeys.add(key)
      }
      return handle
    } catch (error) {
      this.activeGenerationThreads.delete(threadId)
      this.generationStartedAt.delete(threadId)
      throw error
    }
  }

  public async cancelGeneration(
    cancellation: ICodexGenerationCancellation
  ): Promise<void> {
    if (!isRecord(cancellation)) {
      throw new Error('cancellation must be an object')
    }
    const threadId = requiredString(cancellation.threadId, 'threadId', 200)
    const turnId = requiredString(cancellation.turnId, 'turnId', 200)
    const handle = { threadId, turnId }
    const key = this.generationKey(handle)
    if (this.generationResults.has(key)) {
      return
    }
    if (!this.activeGenerationKeys.has(key)) {
      throw new Error('Unknown Codex generation')
    }
    const { transport } = await this.getConnection()
    try {
      await transport.request('turn/interrupt', { threadId, turnId })
    } finally {
      this.completeGeneration(handle, { outcome: 'cancelled' })
    }
  }

  public waitForGeneration(
    untrustedHandle: ICodexGenerationHandle
  ): Promise<ICodexGenerationResult> {
    const handle = this.validateGenerationHandle(untrustedHandle)
    const key = this.generationKey(handle)
    const completed = this.generationResults.get(key)
    if (completed !== undefined) {
      return Promise.resolve(completed)
    }
    if (!this.activeGenerationKeys.has(key)) {
      return Promise.reject(new Error('Unknown Codex generation'))
    }

    const existingWaiter = this.generationWaiters.get(key)
    if (existingWaiter !== undefined) {
      return Promise.reject(
        new Error('Codex generation already has a pending waiter')
      )
    }

    return new Promise(resolve => {
      const timeout = setTimeout(() => {
        void this.cancelTimedOutGeneration(handle)
      }, this.generationTimeoutMs)
      timeout.unref()
      this.generationWaiters.set(key, { resolve, timeout })
    })
  }

  public onNotification(listener: CodexNotificationListener): () => void {
    this.notificationListeners.add(listener)
    return () => this.notificationListeners.delete(listener)
  }

  public onAccountStateChanged(
    listener: CodexAccountStateListener
  ): () => void {
    this.accountStateListeners.add(listener)
    return () => this.accountStateListeners.delete(listener)
  }

  public onRateLimitStateChanged(
    listener: CodexRateLimitStateListener
  ): () => void {
    this.rateLimitStateListeners.add(listener)
    return () => this.rateLimitStateListeners.delete(listener)
  }

  public async shutdown() {
    this.failActiveGenerations()
    this.connection?.transport.close()
    this.connection = undefined
    await this.supervisor.shutdown()
  }

  private async getConnection(): Promise<ICodexConnection> {
    if (this.connection?.transport.isClosed) {
      this.connection = undefined
      await this.supervisor.shutdown()
    }

    const process = await this.supervisor.start()
    if (
      this.connection?.process === process &&
      !this.connection.transport.isClosed
    ) {
      return this.connection
    }
    if (this.connecting !== undefined) {
      return this.connecting
    }

    const connecting = this.initializeConnection(process)
    this.connecting = connecting
    void connecting
      .finally(() => {
        if (this.connecting === connecting) {
          this.connecting = undefined
        }
      })
      .catch(() => undefined)
    return connecting
  }

  private async initializeConnection(
    process: CodexAppServerProcess
  ): Promise<ICodexConnection> {
    this.connection?.transport.close()
    const transport = new CodexJSONRPCTransport(process.stdout, process.stdin, {
      onProtocolError: error =>
        this.emitLog(
          'warn',
          `Codex protocol warning: ${redactCodexDiagnostic(error.message)}`
        ),
    })
    transport.onNotification(notification => {
      void this.handleAccountNotification(notification)
      void this.handleRateLimitNotification(notification)
      this.handleGenerationNotification(notification)
      for (const listener of this.notificationListeners) {
        try {
          listener(notification)
        } catch (error) {
          this.emitLog(
            'error',
            `Codex notification handler failed: ${redactCodexDiagnostic(
              error instanceof Error ? error.message : String(error)
            )}`
          )
        }
      }
    })
    process.once('exit', () => this.failActiveGenerations())

    try {
      await transport.request('initialize', {
        clientInfo: {
          name: 'wingit',
          title: 'WinGit',
          version: this.clientVersion,
        },
        capabilities: { experimentalApi: false },
      })
      transport.notify('initialized')
      const connection = { process, transport }
      this.connection = connection
      return connection
    } catch (error) {
      transport.close()
      await this.supervisor.shutdown()
      throw error
    }
  }

  private validateGenerationRequest(request: ICodexGenerationRequest) {
    if (!isRecord(request)) {
      throw new Error('request must be an object')
    }
    requiredString(request.instructions, 'instructions')
    requiredString(request.prompt, 'prompt')
    if (request.model !== undefined) {
      requiredString(request.model, 'model', 200)
    }
    if (request.outputSchema !== undefined) {
      validateJSONValue(request.outputSchema)
      const serializedSchema = JSON.stringify(request.outputSchema)
      if (serializedSchema.length > 256 * 1024) {
        throw new Error('outputSchema exceeds 262144 characters')
      }
    }
  }

  private validateGenerationHandle(
    value: ICodexGenerationHandle
  ): ICodexGenerationHandle {
    if (!isRecord(value)) {
      throw new Error('handle must be an object')
    }
    return {
      threadId: requiredString(value.threadId, 'threadId', 200),
      turnId: requiredString(value.turnId, 'turnId', 200),
    }
  }

  private generationKey(handle: ICodexGenerationHandle) {
    return `${handle.threadId}\u0000${handle.turnId}`
  }

  private handleGenerationNotification(notification: ICodexServerNotification) {
    if (notification.method !== 'turn/completed') {
      return
    }

    const params = notification.params
    if (!isRecord(params) || typeof params.threadId !== 'string') {
      this.emitLog('warn', 'Codex returned an invalid completed turn')
      return
    }
    if (!this.activeGenerationThreads.has(params.threadId)) {
      return
    }
    const turn = params.turn
    if (
      !isRecord(turn) ||
      typeof turn.id !== 'string' ||
      !Array.isArray(turn.items) ||
      typeof turn.status !== 'string'
    ) {
      this.emitLog('warn', 'Codex returned an invalid completed turn')
      return
    }

    const handle = { threadId: params.threadId, turnId: turn.id }
    if (turn.status === 'interrupted') {
      this.completeGeneration(handle, { outcome: 'cancelled' })
      return
    }
    if (turn.status === 'failed') {
      this.completeGeneration(handle, this.mapFailedTurn(turn.error))
      return
    }
    if (turn.status !== 'completed') {
      this.completeGeneration(handle, { outcome: 'runtime-error' })
      return
    }

    const messages = turn.items.filter(
      item =>
        isRecord(item) &&
        item.type === 'agentMessage' &&
        typeof item.text === 'string' &&
        item.text.length > 0
    )
    const finalMessage =
      [...messages]
        .reverse()
        .find(message => message.phase === 'final_answer') ??
      [...messages]
        .reverse()
        .find(message => message.phase === null || message.phase === undefined)

    this.completeGeneration(
      handle,
      finalMessage === undefined
        ? { outcome: 'runtime-error' }
        : { outcome: 'success', output: finalMessage.text as string }
    )
  }

  private mapFailedTurn(error: unknown): ICodexGenerationResult {
    const errorInfo = isRecord(error) ? error.codexErrorInfo : undefined
    const message =
      isRecord(error) && typeof error.message === 'string'
        ? error.message.toLowerCase()
        : ''
    const httpStatusCode =
      isRecord(errorInfo) &&
      Object.values(errorInfo).some(
        value => isRecord(value) && value.httpStatusCode === 401
      )
        ? 401
        : isRecord(errorInfo) &&
          Object.values(errorInfo).some(
            value => isRecord(value) && value.httpStatusCode === 429
          )
        ? 429
        : undefined

    if (
      errorInfo === 'unauthorized' ||
      httpStatusCode === 401 ||
      /unauthorized|authentication|\b401\b/.test(message)
    ) {
      return { outcome: 'auth-required' }
    }
    if (
      errorInfo === 'usageLimitExceeded' ||
      errorInfo === 'rateLimitExceeded' ||
      errorInfo === 'sessionBudgetExceeded' ||
      httpStatusCode === 429 ||
      /rate.?limit|too many requests|quota|\b429\b/.test(message)
    ) {
      return { outcome: 'rate-limited' }
    }
    return { outcome: 'runtime-error' }
  }

  private completeGeneration(
    handle: ICodexGenerationHandle,
    result: ICodexGenerationResult
  ) {
    const key = this.generationKey(handle)
    if (this.generationResults.has(key)) {
      return
    }

    this.activeGenerationKeys.delete(key)
    this.activeGenerationThreads.delete(handle.threadId)
    const startedAt = this.generationStartedAt.get(handle.threadId)
    this.generationStartedAt.delete(handle.threadId)
    this.generationResults.set(key, result)
    const duration =
      startedAt === undefined
        ? 'unknown duration'
        : `${Math.max(0, Date.now() - startedAt)}ms`
    this.emitLog(
      'info',
      `Codex generation finished (${result.outcome}, ${duration})`
    )
    while (this.generationResults.size > 100) {
      const oldestKey = this.generationResults.keys().next().value as
        | string
        | undefined
      if (oldestKey === undefined) {
        break
      }
      this.generationResults.delete(oldestKey)
    }

    const waiter = this.generationWaiters.get(key)
    if (waiter !== undefined) {
      clearTimeout(waiter.timeout)
      this.generationWaiters.delete(key)
      waiter.resolve(result)
    }
  }

  private async cancelTimedOutGeneration(handle: ICodexGenerationHandle) {
    const key = this.generationKey(handle)
    if (this.generationResults.has(key)) {
      return
    }
    this.completeGeneration(handle, { outcome: 'timeout' })
    try {
      const { transport } = await this.getConnection()
      await transport.request('turn/interrupt', {
        threadId: handle.threadId,
        turnId: handle.turnId,
      })
    } catch {
      this.emitLog(
        'warn',
        'Timed-out Codex generation could not be interrupted'
      )
    }
  }

  private failActiveGenerations() {
    for (const key of [...this.activeGenerationKeys]) {
      const separator = key.indexOf('\u0000')
      this.completeGeneration(
        {
          threadId: key.slice(0, separator),
          turnId: key.slice(separator + 1),
        },
        { outcome: 'runtime-error' }
      )
    }
  }

  private parseLoginStart(
    method: CodexLoginMethod,
    response: CodexJSONValue | undefined
  ): ICodexManagedLoginStart {
    if (!isRecord(response)) {
      throw new Error('Codex returned an invalid login response')
    }
    const loginId = requiredString(response.loginId, 'loginId', 200)

    if (method === 'browser') {
      if (response.type !== 'chatgpt') {
        throw new Error('Codex returned the wrong browser login type')
      }
      return {
        method,
        loginId,
        authorizationUrl: requiredString(
          response.authUrl,
          'authorizationUrl',
          16_384
        ),
      }
    }

    if (response.type !== 'chatgptDeviceCode') {
      throw new Error('Codex returned the wrong device login type')
    }
    return {
      method,
      loginId,
      authorizationUrl: requiredString(
        response.verificationUrl,
        'verificationUrl',
        16_384
      ),
      userCode: requiredString(response.userCode, 'userCode', 100),
    }
  }

  private async handleAccountNotification(
    notification: ICodexServerNotification
  ) {
    if (notification.method === 'account/updated') {
      this.emitAccountState(await this.readAccount(false))
      return
    }
    if (notification.method !== 'account/login/completed') {
      return
    }

    const params = notification.params
    if (!isRecord(params) || typeof params.success !== 'boolean') {
      this.emitAccountState(accountErrorState(new Error('invalid login event')))
      return
    }
    if (!params.success) {
      this.emitAccountState({
        status: 'error',
        type: 'signed-out',
        email: null,
        planType: null,
        requiresOpenaiAuth: true,
        errorMessage: 'ChatGPT sign-in did not complete. Try again.',
      })
      return
    }

    this.emitAccountState(await this.readAccount(false))
  }

  private emitAccountState(state: ICodexAccountState) {
    for (const listener of this.accountStateListeners) {
      try {
        listener(state)
      } catch {
        this.emitLog('error', `Codex account state handler failed`)
      }
    }
  }

  private async handleRateLimitNotification(
    notification: ICodexServerNotification
  ) {
    if (
      notification.method !== 'account/rateLimits/updated' &&
      notification.method !== 'account/updated' &&
      notification.method !== 'account/login/completed'
    ) {
      return
    }

    this.emitRateLimitState(await this.readRateLimits())
  }

  private emitRateLimitState(state: ICodexRateLimitState) {
    for (const listener of this.rateLimitStateListeners) {
      try {
        listener(state)
      } catch {
        this.emitLog('error', 'Codex rate-limit state handler failed')
      }
    }
  }

  private emitLog(level: 'info' | 'warn' | 'error', message: string) {
    try {
      void Promise.resolve(this.log(level, message)).catch(() => undefined)
    } catch {
      // Diagnostics must not interrupt protocol traffic.
    }
  }
}
