import {
  ChildProcessWithoutNullStreams,
  SpawnOptionsWithoutStdio,
  spawn,
} from 'child_process'
import { mkdir } from 'fs/promises'
import { join } from 'path'

export type CodexAppServerState =
  | 'stopped'
  | 'starting'
  | 'running'
  | 'restarting'
  | 'stopping'
  | 'failed'

export interface ICodexAppServerSnapshot {
  readonly state: CodexAppServerState
  readonly pid: number | undefined
  readonly startupFailures: number
  readonly unexpectedExits: number
  readonly lastError: Error | undefined
}

export type CodexAppServerProcess = ChildProcessWithoutNullStreams

type SpawnCodexAppServer = (
  executablePath: string,
  args: ReadonlyArray<string>,
  options: SpawnOptionsWithoutStdio
) => CodexAppServerProcess

type CodexLogLevel = 'info' | 'warn' | 'error'

export interface ICodexAppServerSupervisorOptions {
  readonly executablePath: string
  readonly codexHomePath: string
  readonly args?: ReadonlyArray<string>
  readonly workingDirectory?: string
  readonly spawnProcess?: SpawnCodexAppServer
  readonly prepareCodexHome?: (path: string) => Promise<void>
  readonly log?: (level: CodexLogLevel, message: string) => void | Promise<void>
  readonly onFailure?: (error: Error) => void
  readonly onStateChanged?: (snapshot: ICodexAppServerSnapshot) => void
  readonly startupTimeoutMs?: number
  readonly startupGraceMs?: number
  readonly shutdownTimeoutMs?: number
  readonly stableRunMs?: number
  readonly maxStartupAttempts?: number
  readonly maxUnexpectedExits?: number
  readonly retryDelayMs?: (attempt: number) => number
}

export class CodexAppServerStartError extends Error {
  public constructor(message: string) {
    super(message)
    this.name = 'CodexAppServerStartError'
  }
}

export class CodexAppServerCrashError extends Error {
  public constructor(message: string) {
    super(message)
    this.name = 'CodexAppServerCrashError'
  }
}

class CodexAppServerStoppedError extends Error {
  public constructor() {
    super('Codex App Server was stopped before startup completed.')
    this.name = 'CodexAppServerStoppedError'
  }
}

const credentialEnvironmentVariables = [
  'CHATGPT_ACCESS_TOKEN',
  'CODEX_ACCESS_TOKEN',
  'OPENAI_API_KEY',
  'OPENAI_ORG_ID',
  'OPENAI_PROJECT_ID',
] as const

const defaultRetryDelayMs = (attempt: number) =>
  Math.min(250 * Math.pow(2, attempt - 1), 2_000)

const defaultPrepareCodexHome = (path: string) =>
  mkdir(path, { recursive: true }).then(() => undefined)

const wait = (milliseconds: number) =>
  new Promise<void>(resolve => setTimeout(resolve, milliseconds))

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error))
}

function requirePositiveInteger(name: string, value: number) {
  if (!Number.isInteger(value) || value < 1) {
    throw new Error(`${name} must be a positive integer`)
  }
  return value
}

function requireNonNegativeInteger(name: string, value: number) {
  if (!Number.isInteger(value) || value < 0) {
    throw new Error(`${name} must be a non-negative integer`)
  }
  return value
}

/**
 * Removes common credential forms before App Server diagnostics reach WinGit's
 * logs. The child never receives credential-bearing environment variables,
 * but the server may still describe stored account state on stderr.
 */
export function redactCodexDiagnostic(message: string): string {
  return message
    .replace(/\bBearer\s+[^\s,;]+/gi, 'Bearer [REDACTED]')
    .replace(
      /(authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|cookie)(\s*[=:]\s*)(["']?)[^\s,;"']+\3/gi,
      '$1$2[REDACTED]'
    )
    .replace(/\bsk-[A-Za-z0-9_-]{12,}\b/g, '[REDACTED]')
}

export function createCodexAppServerEnvironment(
  codexHomePath: string,
  parentEnvironment: NodeJS.ProcessEnv = process.env
): NodeJS.ProcessEnv {
  const childEnvironment: NodeJS.ProcessEnv = {
    ...parentEnvironment,
    CODEX_HOME: codexHomePath,
    RUST_LOG: 'error',
  }

  for (const credentialName of credentialEnvironmentVariables) {
    delete childEnvironment[credentialName]
  }

  return childEnvironment
}

export function getBundledCodexExecutablePath(
  applicationRoot: string,
  platform: NodeJS.Platform = process.platform,
  architecture: string = process.arch
): string {
  if (platform !== 'win32') {
    throw new Error(`Codex App Server is not packaged for ${platform}`)
  }

  const targetTriple =
    architecture === 'x64'
      ? 'x86_64-pc-windows-msvc'
      : architecture === 'arm64'
      ? 'aarch64-pc-windows-msvc'
      : undefined

  if (targetTriple === undefined) {
    throw new Error(
      `Codex App Server is not packaged for Windows ${architecture}`
    )
  }

  return join(
    applicationRoot,
    'codex',
    'vendor',
    targetTriple,
    'bin',
    'codex.exe'
  )
}

/**
 * Owns the single Codex App Server process used by WinGit. Protocol framing is
 * deliberately outside this class so process lifecycle and JSON-RPC transport
 * can be tested independently.
 */
export class CodexAppServerSupervisor {
  private readonly executablePath: string
  private readonly codexHomePath: string
  private readonly args: ReadonlyArray<string>
  private readonly workingDirectory: string
  private readonly spawnProcess: SpawnCodexAppServer
  private readonly prepareCodexHome: (path: string) => Promise<void>
  private readonly log: NonNullable<ICodexAppServerSupervisorOptions['log']>
  private readonly onFailure:
    | NonNullable<ICodexAppServerSupervisorOptions['onFailure']>
    | undefined
  private readonly onStateChanged:
    | NonNullable<ICodexAppServerSupervisorOptions['onStateChanged']>
    | undefined
  private readonly startupTimeoutMs: number
  private readonly startupGraceMs: number
  private readonly shutdownTimeoutMs: number
  private readonly stableRunMs: number
  private readonly maxStartupAttempts: number
  private readonly maxUnexpectedExits: number
  private readonly retryDelayMs: (attempt: number) => number

  private state: CodexAppServerState = 'stopped'
  private child: CodexAppServerProcess | undefined
  private startPromise: Promise<CodexAppServerProcess> | undefined
  private shutdownPromise: Promise<void> | undefined
  private desiredRunning = false
  private lifecycle = 0
  private startupFailures = 0
  private unexpectedExits = 0
  private lastError: Error | undefined
  private stableRunTimer: NodeJS.Timeout | undefined

  public constructor(options: ICodexAppServerSupervisorOptions) {
    this.executablePath = options.executablePath
    this.codexHomePath = options.codexHomePath
    this.args = options.args ?? ['app-server', '--stdio']
    this.workingDirectory =
      options.workingDirectory ?? join(this.executablePath, '..', '..', '..')
    this.spawnProcess = options.spawnProcess ?? spawn
    this.prepareCodexHome = options.prepareCodexHome ?? defaultPrepareCodexHome
    this.log = options.log ?? (() => undefined)
    this.onFailure = options.onFailure
    this.onStateChanged = options.onStateChanged
    this.startupTimeoutMs = requirePositiveInteger(
      'startupTimeoutMs',
      options.startupTimeoutMs ?? 10_000
    )
    this.startupGraceMs = requireNonNegativeInteger(
      'startupGraceMs',
      options.startupGraceMs ?? 250
    )
    this.shutdownTimeoutMs = requirePositiveInteger(
      'shutdownTimeoutMs',
      options.shutdownTimeoutMs ?? 5_000
    )
    this.stableRunMs = requireNonNegativeInteger(
      'stableRunMs',
      options.stableRunMs ?? 30_000
    )
    this.maxStartupAttempts = requirePositiveInteger(
      'maxStartupAttempts',
      options.maxStartupAttempts ?? 3
    )
    this.maxUnexpectedExits = requirePositiveInteger(
      'maxUnexpectedExits',
      options.maxUnexpectedExits ?? 3
    )
    this.retryDelayMs = options.retryDelayMs ?? defaultRetryDelayMs
  }

  public get snapshot(): ICodexAppServerSnapshot {
    return {
      state: this.state,
      pid: this.child?.pid,
      startupFailures: this.startupFailures,
      unexpectedExits: this.unexpectedExits,
      lastError: this.lastError,
    }
  }

  /** Starts the server on demand and shares one startup across all callers. */
  public start(): Promise<CodexAppServerProcess> {
    const pendingShutdown = this.shutdownPromise
    if (pendingShutdown !== undefined) {
      return pendingShutdown.then(() => {
        if (this.shutdownPromise === pendingShutdown) {
          this.shutdownPromise = undefined
        }
        return this.start()
      })
    }

    this.desiredRunning = true

    if (this.child !== undefined && this.state === 'running') {
      return Promise.resolve(this.child)
    }

    if (this.startPromise !== undefined) {
      return this.startPromise
    }

    if (this.state === 'failed' || this.state === 'stopped') {
      this.startupFailures = 0
      this.unexpectedExits = 0
      this.lastError = undefined
    }

    return this.beginStart(this.lifecycle)
  }

  /** Stops startup, restart work, and the active child. Safe to call repeatedly. */
  public shutdown(): Promise<void> {
    if (this.shutdownPromise !== undefined) {
      return this.shutdownPromise
    }

    const promise = this.shutdownInternal()
    this.shutdownPromise = promise
    void promise
      .finally(() => {
        if (this.shutdownPromise === promise) {
          this.shutdownPromise = undefined
        }
      })
      .catch(() => undefined)
    return promise
  }

  private beginStart(lifecycle: number): Promise<CodexAppServerProcess> {
    const promise = this.startWithRetries(lifecycle)
    return this.trackStart(promise)
  }

  private trackStart(
    promise: Promise<CodexAppServerProcess>
  ): Promise<CodexAppServerProcess> {
    this.startPromise = promise
    void promise.catch(() => undefined)
    void promise
      .finally(() => {
        if (this.startPromise === promise) {
          this.startPromise = undefined
        }
      })
      .catch(() => undefined)
    return promise
  }

  private async startWithRetries(
    lifecycle: number
  ): Promise<CodexAppServerProcess> {
    let lastStartupError: Error | undefined

    for (let attempt = 1; attempt <= this.maxStartupAttempts; attempt++) {
      this.assertShouldRun(lifecycle)
      this.setState(attempt === 1 ? 'starting' : 'restarting')

      try {
        await this.prepareCodexHome(this.codexHomePath)
        this.assertShouldRun(lifecycle)
        const child = await this.startOnce(lifecycle)
        this.startupFailures = 0
        this.lastError = undefined
        this.setState('running')
        this.armStableRunTimer(child)
        return child
      } catch (error) {
        if (!this.shouldRun(lifecycle)) {
          throw new CodexAppServerStoppedError()
        }

        lastStartupError = asError(error)
        this.lastError = lastStartupError
        this.startupFailures = attempt
        this.emitLog(
          'warn',
          `Codex App Server startup attempt ${attempt} failed: ${redactCodexDiagnostic(
            lastStartupError.message
          )}`
        )
        this.emitStateChanged()

        if (attempt < this.maxStartupAttempts) {
          await wait(this.retryDelayMs(attempt))
        }
      }
    }

    const error = new CodexAppServerStartError(
      `Codex App Server could not start after ${this.maxStartupAttempts} attempts. ` +
        `Verify the bundled runtime at "${this.executablePath}" and try again. ` +
        `Last error: ${redactCodexDiagnostic(
          lastStartupError?.message ?? 'unknown startup failure'
        )}`
    )
    this.fail(error)
    throw error
  }

  private startOnce(lifecycle: number): Promise<CodexAppServerProcess> {
    let child: CodexAppServerProcess
    try {
      child = this.spawnProcess(this.executablePath, this.args, {
        cwd: this.workingDirectory,
        env: createCodexAppServerEnvironment(this.codexHomePath),
        stdio: ['pipe', 'pipe', 'pipe'],
        windowsHide: true,
      })
    } catch (error) {
      return Promise.reject(asError(error))
    }

    this.child = child
    this.captureStderr(child)

    return new Promise<CodexAppServerProcess>((resolve, reject) => {
      let settled = false
      let graceTimer: NodeJS.Timeout | undefined

      const cleanup = () => {
        clearTimeout(startupTimer)
        if (graceTimer !== undefined) {
          clearTimeout(graceTimer)
        }
        child.removeListener('spawn', onSpawn)
        child.removeListener('error', onError)
        child.removeListener('exit', onStartupExit)
      }

      const failStartup = (error: Error) => {
        if (settled) {
          return
        }
        settled = true
        cleanup()
        void this.stopProcess(child).then(
          () => {
            if (this.child === child) {
              this.child = undefined
            }
            reject(error)
          },
          stopError => {
            if (this.child === child) {
              this.child = undefined
            }
            const stopMessage = redactCodexDiagnostic(
              asError(stopError).message
            )
            this.emitLog(
              'error',
              `Failed to stop an unsuccessful Codex App Server process: ${stopMessage}`
            )
            reject(
              new Error(`${error.message}; cleanup failed: ${stopMessage}`)
            )
          }
        )
      }

      const markStarted = () => {
        if (settled) {
          return
        }
        if (!this.shouldRun(lifecycle)) {
          failStartup(new CodexAppServerStoppedError())
          return
        }
        settled = true
        cleanup()
        child.on('error', error =>
          this.emitLog(
            'error',
            `Codex App Server process error: ${redactCodexDiagnostic(
              asError(error).message
            )}`
          )
        )
        child.once('exit', (code, signal) =>
          this.handleUnexpectedExit(child, code, signal)
        )
        resolve(child)
      }

      const onSpawn = () => {
        if (this.startupGraceMs === 0) {
          markStarted()
        } else {
          graceTimer = setTimeout(markStarted, this.startupGraceMs)
          graceTimer.unref()
        }
      }
      const onError = (error: Error) => failStartup(error)
      const onStartupExit = (
        code: number | null,
        signal: NodeJS.Signals | null
      ) =>
        failStartup(
          new Error(
            `process exited during startup (code ${String(
              code
            )}, signal ${String(signal)})`
          )
        )

      const startupTimer = setTimeout(
        () =>
          failStartup(
            new Error(`startup timed out after ${this.startupTimeoutMs}ms`)
          ),
        this.startupTimeoutMs
      )
      startupTimer.unref()

      child.once('spawn', onSpawn)
      child.once('error', onError)
      child.once('exit', onStartupExit)
    })
  }

  private handleUnexpectedExit(
    child: CodexAppServerProcess,
    code: number | null,
    signal: NodeJS.Signals | null
  ) {
    if (this.child !== child) {
      return
    }

    this.clearStableRunTimer()
    this.child = undefined

    if (!this.desiredRunning || this.state === 'stopping') {
      return
    }

    this.unexpectedExits++
    const exitDescription = `code ${String(code)}, signal ${String(signal)}`

    if (this.unexpectedExits >= this.maxUnexpectedExits) {
      const error = new CodexAppServerCrashError(
        `Codex App Server stopped after ${this.unexpectedExits} unexpected exits ` +
          `(${exitDescription}). Restart it from WinGit's Codex settings.`
      )
      this.fail(error)
      return
    }

    this.lastError = new CodexAppServerCrashError(
      `Codex App Server exited unexpectedly (${exitDescription}).`
    )
    this.emitLog('warn', this.lastError.message)
    this.setState('restarting')

    const lifecycle = this.lifecycle
    const restart = wait(this.retryDelayMs(this.unexpectedExits)).then(() => {
      this.assertShouldRun(lifecycle)
      return this.startWithRetries(lifecycle)
    })
    this.trackStart(restart)
    void restart.catch(error => {
      if (
        this.shouldRun(lifecycle) &&
        !(error instanceof CodexAppServerStartError)
      ) {
        this.fail(asError(error))
      }
    })
  }

  private armStableRunTimer(child: CodexAppServerProcess) {
    this.clearStableRunTimer()
    if (this.stableRunMs === 0) {
      this.unexpectedExits = 0
      return
    }

    this.stableRunTimer = setTimeout(() => {
      if (this.child === child && this.state === 'running') {
        this.unexpectedExits = 0
        this.emitStateChanged()
      }
    }, this.stableRunMs)
    this.stableRunTimer.unref()
  }

  private clearStableRunTimer() {
    if (this.stableRunTimer !== undefined) {
      clearTimeout(this.stableRunTimer)
      this.stableRunTimer = undefined
    }
  }

  private captureStderr(child: CodexAppServerProcess) {
    let remainder = ''
    child.stderr.setEncoding('utf8')
    child.stderr.on('data', chunk => {
      const lines = `${remainder}${String(chunk)}`.split(/\r?\n/)
      remainder = lines.pop() ?? ''
      for (const line of lines) {
        this.logStderrLine(line)
      }
    })
    child.stderr.on('end', () => {
      if (remainder.length > 0) {
        this.logStderrLine(remainder)
        remainder = ''
      }
    })
  }

  private logStderrLine(line: string) {
    const redactedLine = redactCodexDiagnostic(line).slice(0, 4_000)
    if (redactedLine.length > 0) {
      this.emitLog('warn', `[Codex App Server] ${redactedLine}`)
    }
  }

  private async shutdownInternal() {
    this.desiredRunning = false
    this.lifecycle++
    this.clearStableRunTimer()
    this.setState('stopping')

    const child = this.child
    this.child = undefined
    if (child !== undefined) {
      try {
        await this.stopProcess(child)
      } catch (error) {
        const shutdownError = new Error(
          `Codex App Server did not stop cleanly: ${redactCodexDiagnostic(
            asError(error).message
          )}`
        )
        this.fail(shutdownError)
        throw shutdownError
      }
    }

    this.lastError = undefined
    this.startupFailures = 0
    this.unexpectedExits = 0
    this.setState('stopped')
  }

  private async stopProcess(child: CodexAppServerProcess) {
    if (child.exitCode !== null || child.signalCode !== null) {
      return
    }

    const waitForExit = (timeoutMs: number) =>
      new Promise<boolean>(resolve => {
        let settled = false
        const finish = (exited: boolean) => {
          if (settled) {
            return
          }
          settled = true
          clearTimeout(timeout)
          child.removeListener('exit', onExit)
          resolve(exited)
        }
        const onExit = () => finish(true)
        const timeout = setTimeout(() => finish(false), timeoutMs)
        timeout.unref()
        child.once('exit', onExit)
      })

    const firstExit = waitForExit(this.shutdownTimeoutMs)
    child.kill()
    if (await firstExit) {
      return
    }

    const forcedExit = waitForExit(this.shutdownTimeoutMs)
    child.kill('SIGKILL')
    if (!(await forcedExit)) {
      throw new Error(
        `process ${String(child.pid)} remained alive after SIGKILL`
      )
    }
  }

  private shouldRun(lifecycle: number) {
    return this.desiredRunning && lifecycle === this.lifecycle
  }

  private assertShouldRun(lifecycle: number) {
    if (!this.shouldRun(lifecycle)) {
      throw new CodexAppServerStoppedError()
    }
  }

  private fail(error: Error) {
    this.desiredRunning = false
    this.lastError = error
    this.setState('failed')
    this.emitLog('error', error.message)
    this.onFailure?.(error)
  }

  private setState(state: CodexAppServerState) {
    this.state = state
    this.emitStateChanged()
  }

  private emitStateChanged() {
    this.onStateChanged?.(this.snapshot)
  }

  private emitLog(level: CodexLogLevel, message: string) {
    try {
      void Promise.resolve(this.log(level, message)).catch(() => undefined)
    } catch {
      // Diagnostics must never take down process supervision.
    }
  }
}
