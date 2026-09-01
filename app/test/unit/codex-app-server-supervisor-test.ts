import assert from 'node:assert'
import { EventEmitter } from 'node:events'
import { PassThrough } from 'node:stream'
import { describe, it } from 'node:test'
import {
  CodexAppServerCrashError,
  CodexAppServerProcess,
  CodexAppServerStartError,
  CodexAppServerSupervisor,
  ICodexAppServerSupervisorOptions,
  createCodexAppServerEnvironment,
  getBundledCodexExecutablePath,
  redactCodexDiagnostic,
} from '../../src/main-process/codex-app-server-supervisor'

class FakeCodexProcess extends EventEmitter {
  public readonly stdin = new PassThrough()
  public readonly stdout = new PassThrough()
  public readonly stderr = new PassThrough()
  public readonly pid: number
  public exitCode: number | null = null
  public signalCode: NodeJS.Signals | null = null
  public killCalls = 0
  public exitOnKill = true

  public constructor(pid: number) {
    super()
    this.pid = pid
  }

  public spawn() {
    this.emit('spawn')
  }

  public crash(code: number) {
    this.exitCode = code
    this.emit('exit', code, null)
  }

  public kill(signal: NodeJS.Signals = 'SIGTERM') {
    this.killCalls++
    if (this.exitOnKill && this.exitCode === null && this.signalCode === null) {
      this.finishSignal(signal)
    }
    return true
  }

  public finishSignal(signal: NodeJS.Signals) {
    this.signalCode = signal
    this.emit('exit', null, signal)
  }
}

type SpawnCodexAppServer = NonNullable<
  ICodexAppServerSupervisorOptions['spawnProcess']
>

const asCodexProcess = (child: FakeCodexProcess) =>
  child as unknown as CodexAppServerProcess

async function waitFor(condition: () => boolean) {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (condition()) {
      return
    }
    await new Promise(resolve => setTimeout(resolve, 1))
  }
  assert.fail('Timed out waiting for condition')
}

function createSuccessfulSpawn(
  children: FakeCodexProcess[],
  inspect?: Parameters<SpawnCodexAppServer>[2] extends infer T
    ? (args: ReadonlyArray<string>, options: T) => void
    : never
): SpawnCodexAppServer {
  return (_executablePath, args, options) => {
    inspect?.(args, options)
    const child = new FakeCodexProcess(1_000 + children.length)
    children.push(child)
    process.nextTick(() => child.spawn())
    return asCodexProcess(child)
  }
}

describe('CodexAppServerSupervisor', () => {
  it('shares one process across concurrent starts and shuts it down', async () => {
    const children = new Array<FakeCodexProcess>()
    let receivedCodexHome: string | undefined
    const spawnProcess = createSuccessfulSpawn(children, (args, options) => {
      assert.deepStrictEqual(args, ['app-server', '--stdio'])
      receivedCodexHome = options.env?.CODEX_HOME
      assert.deepStrictEqual(options.stdio, ['pipe', 'pipe', 'pipe'])
      assert.equal(options.windowsHide, true)
    })
    const supervisor = new CodexAppServerSupervisor({
      executablePath: 'C:\\WinGit\\codex.exe',
      codexHomePath: 'C:\\WinGit Data\\codex',
      spawnProcess,
      prepareCodexHome: async () => undefined,
      startupGraceMs: 0,
    })

    const firstStart = supervisor.start()
    const secondStart = supervisor.start()
    assert.strictEqual(firstStart, secondStart)

    const [firstChild, secondChild] = await Promise.all([
      firstStart,
      secondStart,
    ])
    assert.strictEqual(firstChild, secondChild)
    assert.equal(children.length, 1)
    assert.equal(receivedCodexHome, 'C:\\WinGit Data\\codex')
    assert.equal(supervisor.snapshot.state, 'running')

    await supervisor.shutdown()
    assert.equal(children[0].killCalls, 1)
    assert.equal(supervisor.snapshot.state, 'stopped')
    assert.equal(supervisor.snapshot.pid, undefined)
  })

  it('supports an explicit executable contract for deterministic tests', async () => {
    const children = new Array<FakeCodexProcess>()
    const spawnProcess = createSuccessfulSpawn(children, (args, options) => {
      assert.deepStrictEqual(args, ['C:\\WinGit\\fake-server.cjs'])
      assert.equal(options.cwd, 'C:\\WinGit')
    })
    const supervisor = new CodexAppServerSupervisor({
      executablePath: 'C:\\Node\\node.exe',
      codexHomePath: 'C:\\WinGit Data\\codex',
      args: ['C:\\WinGit\\fake-server.cjs'],
      workingDirectory: 'C:\\WinGit',
      spawnProcess,
      prepareCodexHome: async () => undefined,
      startupGraceMs: 0,
    })

    await supervisor.start()
    await supervisor.shutdown()
  })

  it('stops after three startup failures and reports one actionable error', async () => {
    const children = new Array<FakeCodexProcess>()
    const failures = new Array<Error>()
    const spawnProcess: SpawnCodexAppServer = () => {
      const child = new FakeCodexProcess(2_000 + children.length)
      children.push(child)
      process.nextTick(() => child.emit('error', new Error('missing runtime')))
      return asCodexProcess(child)
    }
    const supervisor = new CodexAppServerSupervisor({
      executablePath: 'C:\\WinGit\\codex.exe',
      codexHomePath: 'C:\\WinGit Data\\codex',
      spawnProcess,
      prepareCodexHome: async () => undefined,
      startupGraceMs: 0,
      retryDelayMs: () => 0,
      onFailure: error => failures.push(error),
    })

    await assert.rejects(supervisor.start(), error => {
      assert(error instanceof CodexAppServerStartError)
      assert.match(error.message, /could not start after 3 attempts/)
      assert.match(error.message, /Verify the bundled runtime/)
      return true
    })

    assert.equal(children.length, 3)
    assert(children.every(child => child.killCalls === 1))
    assert.equal(failures.length, 1)
    assert.strictEqual(failures[0], supervisor.snapshot.lastError)
    assert.equal(supervisor.snapshot.state, 'failed')
  })

  it('waits for shutdown before starting a replacement process', async () => {
    const children = new Array<FakeCodexProcess>()
    const supervisor = new CodexAppServerSupervisor({
      executablePath: 'C:\\WinGit\\codex.exe',
      codexHomePath: 'C:\\WinGit Data\\codex',
      spawnProcess: createSuccessfulSpawn(children),
      prepareCodexHome: async () => undefined,
      startupGraceMs: 0,
    })

    await supervisor.start()
    children[0].exitOnKill = false
    const shutdown = supervisor.shutdown()
    const restart = supervisor.start()

    await new Promise(resolve => setImmediate(resolve))
    assert.equal(children.length, 1)

    children[0].finishSignal('SIGTERM')
    await shutdown
    const replacement = await restart
    assert.strictEqual(replacement, asCodexProcess(children[1]))
    assert.equal(children.length, 2)

    await supervisor.shutdown()
  })

  it('bounds automatic restarts after repeated unexpected exits', async () => {
    const children = new Array<FakeCodexProcess>()
    const failures = new Array<Error>()
    const supervisor = new CodexAppServerSupervisor({
      executablePath: 'C:\\WinGit\\codex.exe',
      codexHomePath: 'C:\\WinGit Data\\codex',
      spawnProcess: createSuccessfulSpawn(children),
      prepareCodexHome: async () => undefined,
      startupGraceMs: 0,
      retryDelayMs: () => 0,
      stableRunMs: 60_000,
      maxStartupAttempts: 1,
      maxUnexpectedExits: 3,
      onFailure: error => failures.push(error),
    })

    await supervisor.start()
    children[0].crash(9)
    await waitFor(
      () => children.length === 2 && supervisor.snapshot.state === 'running'
    )
    children[1].crash(9)
    await waitFor(
      () => children.length === 3 && supervisor.snapshot.state === 'running'
    )
    children[2].crash(9)

    assert.equal(children.length, 3)
    assert.equal(supervisor.snapshot.state, 'failed')
    assert.equal(failures.length, 1)
    assert(failures[0] instanceof CodexAppServerCrashError)
    assert.match(failures[0].message, /after 3 unexpected exits/)
  })

  it('redacts credentials from diagnostics and child environment', () => {
    const diagnostic = redactCodexDiagnostic(
      'Authorization=Bearer secret-token api_key=secret-key ' +
        'access_token:"secret-access" sk-proj-secretvalue12345'
    )
    assert(!diagnostic.includes('secret-token'))
    assert(!diagnostic.includes('secret-key'))
    assert(!diagnostic.includes('secret-access'))
    assert(!diagnostic.includes('sk-proj-secretvalue12345'))
    assert.match(diagnostic, /\[REDACTED\]/)

    const environment = createCodexAppServerEnvironment('C:\\codex-home', {
      PATH: 'C:\\Windows',
      OPENAI_API_KEY: 'secret-key',
      CODEX_ACCESS_TOKEN: 'secret-token',
    })
    assert.equal(environment.CODEX_HOME, 'C:\\codex-home')
    assert.equal(environment.PATH, 'C:\\Windows')
    assert.equal(environment.OPENAI_API_KEY, undefined)
    assert.equal(environment.CODEX_ACCESS_TOKEN, undefined)
  })

  it('redacts stderr before forwarding it to logs', async () => {
    const children = new Array<FakeCodexProcess>()
    const logs = new Array<string>()
    const supervisor = new CodexAppServerSupervisor({
      executablePath: 'C:\\WinGit\\codex.exe',
      codexHomePath: 'C:\\WinGit Data\\codex',
      spawnProcess: createSuccessfulSpawn(children),
      prepareCodexHome: async () => undefined,
      startupGraceMs: 0,
      log: (_level, message) => {
        logs.push(message)
      },
    })

    await supervisor.start()
    children[0].stderr.write('access_token=secret-access\n')
    await new Promise(resolve => setImmediate(resolve))

    assert(logs.some(message => message.includes('access_token=[REDACTED]')))
    assert(logs.every(message => !message.includes('secret-access')))
    await supervisor.shutdown()
  })

  it('resolves only the Windows runtimes WinGit packages', () => {
    assert.equal(
      getBundledCodexExecutablePath('C:\\app', 'win32', 'x64'),
      'C:\\app\\codex\\vendor\\x86_64-pc-windows-msvc\\bin\\codex.exe'
    )
    assert.equal(
      getBundledCodexExecutablePath('C:\\app', 'win32', 'arm64'),
      'C:\\app\\codex\\vendor\\aarch64-pc-windows-msvc\\bin\\codex.exe'
    )
    assert.throws(
      () => getBundledCodexExecutablePath('/app', 'linux', 'x64'),
      /not packaged for linux/
    )
  })
})
