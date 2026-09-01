import assert from 'node:assert'
import { EventEmitter } from 'node:events'
import { PassThrough } from 'node:stream'
import { describe, it } from 'node:test'
import {
  CodexAppServerClient,
  ICodexAppServerLifecycle,
} from '../../src/main-process/codex-app-server-client'
import { CodexAppServerProcess } from '../../src/main-process/codex-app-server-supervisor'

class FakeCodexProcess extends EventEmitter {
  public readonly stdin = new PassThrough()
  public readonly stdout = new PassThrough()
  public readonly stderr = new PassThrough()
  public readonly pid = 4_242
  public exitCode: number | null = null
  public signalCode: NodeJS.Signals | null = null

  public kill() {
    return true
  }
}

class FakeCodexLifecycle implements ICodexAppServerLifecycle {
  public readonly child = new FakeCodexProcess()
  public startCalls = 0
  public shutdownCalls = 0

  public async start() {
    this.startCalls++
    return this.child as unknown as CodexAppServerProcess
  }

  public async shutdown() {
    this.shutdownCalls++
  }
}

type ProtocolMessage = Record<string, unknown>
interface ITestProtocolError {
  readonly rpcError: { readonly code: number; readonly message: string }
}

const TestGenerationWorkingDirectory = 'C:\\Test\\Empty Codex Workspace'

function isTestProtocolError(value: unknown): value is ITestProtocolError {
  return typeof value === 'object' && value !== null && 'rpcError' in value
}

function emulateServer(
  child: FakeCodexProcess,
  respond: (message: ProtocolMessage) => unknown
) {
  const messages = new Array<ProtocolMessage>()
  let inputBuffer = ''
  child.stdin.setEncoding('utf8')
  child.stdin.on('data', chunk => {
    inputBuffer += String(chunk)
    let newlineIndex = inputBuffer.indexOf('\n')
    while (newlineIndex !== -1) {
      const line = inputBuffer.slice(0, newlineIndex)
      inputBuffer = inputBuffer.slice(newlineIndex + 1)
      const message = JSON.parse(line) as ProtocolMessage
      messages.push(message)
      if (message.id !== undefined) {
        const result = respond(message)
        child.stdout.write(
          `${JSON.stringify(
            isTestProtocolError(result)
              ? { id: message.id, error: result.rpcError }
              : { id: message.id, result }
          )}\n`
        )
      }
      newlineIndex = inputBuffer.indexOf('\n')
    }
  })
  return messages
}

async function waitFor(condition: () => boolean) {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (condition()) {
      return
    }
    await new Promise(resolve => setTimeout(resolve, 1))
  }
  assert.fail('Timed out waiting for condition')
}

describe('CodexAppServerClient', () => {
  it('treats a returned ChatGPT account as signed in and omits credentials', async () => {
    const lifecycle = new FakeCodexLifecycle()
    const messages = emulateServer(lifecycle.child, message => {
      switch (message.method) {
        case 'initialize':
          return {
            userAgent: 'codex-test',
            codexHome: 'C:\\secret-home',
            platformFamily: 'windows',
            platformOs: 'windows',
          }
        case 'account/read':
          return {
            account: {
              type: 'chatgpt',
              email: 'test.user@example.invalid',
              planType: 'plus',
              accessToken: 'must-not-cross-ipc',
            },
            requiresOpenaiAuth: true,
            refreshToken: 'must-not-cross-ipc',
          }
        default:
          throw new Error(`Unexpected method: ${String(message.method)}`)
      }
    })
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory
    )

    const first = await client.readAccount(false)
    const second = await client.readAccount(true)

    assert.deepStrictEqual(first, {
      status: 'signed-in',
      type: 'chatgpt',
      email: 'test.user@example.invalid',
      planType: 'plus',
      requiresOpenaiAuth: true,
    })
    assert.deepStrictEqual(second, first)
    assert(!JSON.stringify(first).includes('must-not-cross-ipc'))
    assert.equal(
      messages.filter(message => message.method === 'initialize').length,
      1
    )
    assert.equal(
      messages.filter(message => message.method === 'initialized').length,
      1
    )
    await client.shutdown()
  })

  it('starts, completes, cancels, and logs out managed ChatGPT sessions', async () => {
    const lifecycle = new FakeCodexLifecycle()
    let signedIn = false
    const messages = emulateServer(lifecycle.child, message => {
      switch (message.method) {
        case 'initialize':
          return {}
        case 'account/login/start':
          return (message.params as { type: string }).type === 'chatgpt'
            ? {
                type: 'chatgpt',
                loginId: 'browser-login',
                authUrl: 'https://auth.openai.com/oauth/authorize?test=1',
              }
            : {
                type: 'chatgptDeviceCode',
                loginId: 'device-login',
                verificationUrl: 'https://auth.openai.com/codex/device',
                userCode: 'TEST-CODE',
              }
        case 'account/login/cancel':
          return { status: 'canceled' }
        case 'account/read':
          return signedIn
            ? {
                account: {
                  type: 'chatgpt',
                  email: 'test.user@example.invalid',
                  planType: 'plus',
                },
                requiresOpenaiAuth: false,
              }
            : { account: null, requiresOpenaiAuth: true }
        case 'account/logout':
          signedIn = false
          return {}
        case 'account/rateLimits/read':
          return {
            rateLimits: {
              primary: null,
              secondary: null,
              spendControlReached: null,
              rateLimitReachedType: null,
            },
          }
        default:
          throw new Error(`Unexpected method: ${String(message.method)}`)
      }
    })
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory
    )

    assert.deepStrictEqual(await client.startAccountLogin('browser'), {
      method: 'browser',
      loginId: 'browser-login',
      authorizationUrl: 'https://auth.openai.com/oauth/authorize?test=1',
    })
    assert.deepStrictEqual(await client.startAccountLogin('device-code'), {
      method: 'device-code',
      loginId: 'device-login',
      authorizationUrl: 'https://auth.openai.com/codex/device',
      userCode: 'TEST-CODE',
    })
    const browserLogin = messages.find(
      message =>
        message.method === 'account/login/start' &&
        (message.params as { type?: string }).type === 'chatgpt'
    )
    assert.deepStrictEqual(browserLogin?.params, {
      type: 'chatgpt',
      codexStreamlinedLogin: true,
      useHostedLoginSuccessPage: false,
    })
    await client.cancelAccountLogin('device-login')

    const accountStates = new Array<string>()
    client.onAccountStateChanged(state => accountStates.push(state.status))
    signedIn = true
    lifecycle.child.stdout.write(
      '{"method":"account/login/completed","params":{"loginId":"browser-login","success":true,"error":null}}\n'
    )
    await waitFor(() => accountStates.includes('signed-in'))

    assert.equal((await client.logoutAccount()).status, 'signed-out')
    assert(accountStates.includes('signed-out'))
    assert(messages.some(message => message.method === 'account/login/cancel'))
    assert(messages.some(message => message.method === 'account/logout'))
    await client.shutdown()
  })

  it('maps rate-limit and expired responses to safe account states', async () => {
    const lifecycle = new FakeCodexLifecycle()
    let readCount = 0
    emulateServer(lifecycle.child, message => {
      if (message.method === 'initialize') {
        return {}
      }
      readCount++
      return readCount === 1
        ? { rpcError: { code: 429, message: 'rate limit exceeded secret=one' } }
        : {
            rpcError: {
              code: 401,
              message: 'authentication expired secret=two',
            },
          }
    })
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory
    )

    const limited = await client.readAccount()
    const expired = await client.readAccount()

    assert.equal(limited.status, 'rate-limited')
    assert.equal(expired.status, 'expired')
    assert(!JSON.stringify([limited, expired]).includes('secret'))
    await client.shutdown()
  })

  it('reads sanitized subscription limits and refreshes on updates', async () => {
    const lifecycle = new FakeCodexLifecycle()
    const messages = emulateServer(lifecycle.child, message => {
      if (message.method === 'initialize') {
        return {}
      }
      if (message.method === 'account/rateLimits/read') {
        return {
          rateLimits: {
            primary: {
              usedPercent: 82,
              windowDurationMins: 300,
              resetsAt: 1_788_220_800,
            },
            secondary: null,
            credits: { balance: 'must-not-cross-ipc' },
            spendControlReached: false,
            rateLimitReachedType: null,
          },
        }
      }
      throw new Error(`Unexpected method: ${String(message.method)}`)
    })
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory
    )

    const first = await client.readRateLimits()
    assert.equal(first.status, 'near-limit')
    assert(!JSON.stringify(first).includes('must-not-cross-ipc'))

    const updates = new Array<string>()
    client.onRateLimitStateChanged(state => updates.push(state.status))
    lifecycle.child.stdout.write(
      '{"method":"account/rateLimits/updated","params":{"rateLimits":{"primary":{"usedPercent":90}}}}\n'
    )
    await waitFor(() => updates.includes('near-limit'))

    assert.equal(
      messages.filter(message => message.method === 'account/rateLimits/read')
        .length,
      2
    )
    await client.shutdown()
  })

  it('starts and cancels read-only generation using opaque IDs', async () => {
    const lifecycle = new FakeCodexLifecycle()
    const messages = emulateServer(lifecycle.child, message => {
      switch (message.method) {
        case 'initialize':
          return {}
        case 'thread/start':
          return {
            thread: { id: 'thread-test', processHandle: 'not-renderer-data' },
          }
        case 'turn/start':
          return { turn: { id: 'turn-test', secret: 'not-renderer-data' } }
        case 'turn/interrupt':
          return {}
        default:
          throw new Error(`Unexpected method: ${String(message.method)}`)
      }
    })
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory
    )

    const handle = await client.startGeneration({
      instructions: 'Return only a structured commit message.',
      prompt: 'Write a concise commit message.',
      model: 'test-model',
      outputSchema: { type: 'object' },
    })
    assert.deepStrictEqual(handle, {
      threadId: 'thread-test',
      turnId: 'turn-test',
    })
    assert.deepStrictEqual(Object.keys(handle).sort(), ['threadId', 'turnId'])

    const threadStart = messages.find(
      message => message.method === 'thread/start'
    )
    assert.deepStrictEqual(threadStart?.params, {
      cwd: TestGenerationWorkingDirectory,
      runtimeWorkspaceRoots: [],
      approvalPolicy: 'never',
      sandbox: 'read-only',
      ephemeral: true,
      environments: [],
      dynamicTools: [],
      selectedCapabilityRoots: [],
      baseInstructions: 'Return only a structured commit message.',
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
        mcp_servers: {},
      },
      model: 'test-model',
    })
    const turnStart = messages.find(message => message.method === 'turn/start')
    assert.deepStrictEqual(turnStart?.params, {
      threadId: 'thread-test',
      input: [
        {
          type: 'text',
          text: 'Write a concise commit message.',
          text_elements: [],
        },
      ],
      outputSchema: { type: 'object' },
    })

    await client.cancelGeneration(handle)
    assert.deepStrictEqual(
      messages.find(message => message.method === 'turn/interrupt')?.params,
      handle
    )
    assert.deepStrictEqual(await client.waitForGeneration(handle), {
      outcome: 'cancelled',
    })
    await client.shutdown()
  })

  it('uses protocol-compatible isolated thread parameters', async () => {
    const lifecycle = new FakeCodexLifecycle()
    let experimentalApiEnabled = false
    emulateServer(lifecycle.child, message => {
      switch (message.method) {
        case 'initialize': {
          const params = message.params as
            | { capabilities?: { experimentalApi?: boolean } }
            | undefined
          experimentalApiEnabled =
            params?.capabilities?.experimentalApi === true
          return {}
        }
        case 'thread/start': {
          if (!experimentalApiEnabled) {
            return {
              rpcError: {
                code: -32600,
                message:
                  'thread/start.runtimeWorkspaceRoots requires experimentalApi capability',
              },
            }
          }
          const params = message.params as
            | { config?: { tools?: { web_search?: unknown } } }
            | undefined
          return params?.config?.tools?.web_search === null
            ? {
                rpcError: {
                  code: -32600,
                  message: 'invalid tools.web_search configuration',
                },
              }
            : { thread: { id: 'thread-capability' } }
        }
        case 'turn/start':
          return { turn: { id: 'turn-capability' } }
        default:
          throw new Error(`Unexpected method: ${String(message.method)}`)
      }
    })
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory
    )

    await client.startGeneration({
      instructions: 'Return structured output.',
      prompt: 'Describe a synthetic change.',
    })

    assert.equal(experimentalApiEnabled, true)
    await client.shutdown()
  })

  it('returns only the final agent message from a completed turn', async () => {
    const lifecycle = new FakeCodexLifecycle()
    const logs = new Array<string>()
    emulateServer(lifecycle.child, message => {
      switch (message.method) {
        case 'initialize':
          return {}
        case 'thread/start':
          return { thread: { id: 'thread-final' } }
        case 'turn/start':
          return { turn: { id: 'turn-final' } }
        default:
          throw new Error(`Unexpected method: ${String(message.method)}`)
      }
    })
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory,
      (_level, message) => {
        logs.push(message)
      }
    )
    const handle = await client.startGeneration({
      instructions: 'Return structured output.',
      prompt: 'SUPER_PRIVATE_DIFF',
    })

    lifecycle.child.stdout.write(
      `${JSON.stringify({
        method: 'turn/completed',
        params: {
          threadId: handle.threadId,
          turn: {
            id: handle.turnId,
            status: 'completed',
            error: null,
            items: [
              {
                type: 'agentMessage',
                id: 'commentary',
                text: 'private partial response',
                phase: 'commentary',
              },
              {
                type: 'reasoning',
                id: 'reasoning',
                content: ['private reasoning'],
              },
              {
                type: 'agentMessage',
                id: 'final',
                text: '{"title":"Final","description":"PRIVATE_GENERATED"}',
                phase: 'final_answer',
              },
            ],
          },
        },
      })}\n`
    )

    assert.deepStrictEqual(await client.waitForGeneration(handle), {
      outcome: 'success',
      output: '{"title":"Final","description":"PRIVATE_GENERATED"}',
    })
    assert(logs.some(message => /finished \(success, \d+ms\)/.test(message)))
    assert(logs.every(message => !message.includes('SUPER_PRIVATE_DIFF')))
    assert(logs.every(message => !message.includes('PRIVATE_GENERATED')))
    await client.shutdown()
  })

  it('maps authentication and rate-limit failures without exposing details', async () => {
    const lifecycle = new FakeCodexLifecycle()
    let generation = 0
    emulateServer(lifecycle.child, message => {
      switch (message.method) {
        case 'initialize':
          return {}
        case 'thread/start':
          generation++
          return { thread: { id: `thread-${generation}` } }
        case 'turn/start':
          return { turn: { id: `turn-${generation}` } }
        default:
          throw new Error(`Unexpected method: ${String(message.method)}`)
      }
    })
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory
    )

    const auth = await client.startGeneration({
      instructions: 'test',
      prompt: 'test',
    })
    lifecycle.child.stdout.write(
      `${JSON.stringify({
        method: 'turn/completed',
        params: {
          threadId: auth.threadId,
          turn: {
            id: auth.turnId,
            status: 'failed',
            items: [],
            error: {
              message: 'secret authentication detail',
              codexErrorInfo: 'unauthorized',
            },
          },
        },
      })}\n`
    )
    assert.deepStrictEqual(await client.waitForGeneration(auth), {
      outcome: 'auth-required',
    })

    const limited = await client.startGeneration({
      instructions: 'test',
      prompt: 'test',
    })
    lifecycle.child.stdout.write(
      `${JSON.stringify({
        method: 'turn/completed',
        params: {
          threadId: limited.threadId,
          turn: {
            id: limited.turnId,
            status: 'failed',
            items: [],
            error: {
              message: 'secret quota detail',
              codexErrorInfo: 'usageLimitExceeded',
            },
          },
        },
      })}\n`
    )
    assert.deepStrictEqual(await client.waitForGeneration(limited), {
      outcome: 'rate-limited',
    })
    await client.shutdown()
  })

  it('interrupts a timed-out turn and reports process exit safely', async () => {
    const lifecycle = new FakeCodexLifecycle()
    let generation = 0
    const messages = emulateServer(lifecycle.child, message => {
      switch (message.method) {
        case 'initialize':
          return {}
        case 'thread/start':
          generation++
          return { thread: { id: `thread-${generation}` } }
        case 'turn/start':
          return { turn: { id: `turn-${generation}` } }
        case 'turn/interrupt':
          return {}
        default:
          throw new Error(`Unexpected method: ${String(message.method)}`)
      }
    })
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory,
      () => undefined,
      5
    )

    const timedOut = await client.startGeneration({
      instructions: 'test',
      prompt: 'test',
    })
    assert.deepStrictEqual(await client.waitForGeneration(timedOut), {
      outcome: 'timeout',
    })
    await waitFor(() =>
      messages.some(message => message.method === 'turn/interrupt')
    )

    const crashed = await client.startGeneration({
      instructions: 'test',
      prompt: 'test',
    })
    const result = client.waitForGeneration(crashed)
    lifecycle.child.emit('exit', 1, null)
    assert.deepStrictEqual(await result, { outcome: 'runtime-error' })
    await client.shutdown()
  })

  it('delivers protocol notifications only to main-process listeners', async () => {
    const lifecycle = new FakeCodexLifecycle()
    emulateServer(lifecycle.child, message =>
      message.method === 'account/read'
        ? { account: null, requiresOpenaiAuth: true }
        : {}
    )
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory
    )
    const methods = new Array<string>()
    client.onNotification(notification => methods.push(notification.method))

    await client.readAccount()
    lifecycle.child.stdout.write(
      '{"method":"item/agentMessage/delta","params":{"delta":"hello"}}\n'
    )

    assert.deepStrictEqual(methods, ['item/agentMessage/delta'])
    await client.shutdown()
  })

  it('rejects malformed renderer inputs before starting Codex', async () => {
    const lifecycle = new FakeCodexLifecycle()
    const client = new CodexAppServerClient(
      lifecycle,
      'test-version',
      TestGenerationWorkingDirectory
    )

    await assert.rejects(
      client.startGeneration({
        instructions: '',
        prompt: 'test',
      }),
      /instructions must be a non-empty string/
    )
    await assert.rejects(
      client.readAccount('yes' as unknown as boolean),
      /refreshToken must be a boolean/
    )
    assert.equal(lifecycle.startCalls, 0)
  })
})
