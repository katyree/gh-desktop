import { ChildProcessWithoutNullStreams, spawn } from 'child_process'
import { cp, mkdir, mkdtemp, readFile, readdir, rm, stat } from 'fs/promises'
import { createRequire } from 'module'
import { dirname, join, resolve, sep } from 'path'
import { createInterface } from 'readline'
import { tmpdir } from 'os'

interface IPackageJSON {
  readonly version: string
  readonly dependencies?: Readonly<Record<string, string>>
}

interface IRPCResponse {
  readonly id: number
  readonly result?: unknown
  readonly error?: unknown
}

interface IPendingRequest {
  readonly resolve: (response: IRPCResponse) => void
  readonly reject: (error: Error) => void
  readonly timeout: NodeJS.Timeout
}

const projectRoot = dirname(__dirname)
const appRoot = join(projectRoot, 'app')
const appRequire = createRequire(join(appRoot, 'package.json'))
const requestTimeoutMs = 15_000

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

async function readPackageJSON(path: string): Promise<IPackageJSON> {
  return JSON.parse(await readFile(path, 'utf8'))
}

function getWindowsTarget() {
  if (process.platform !== 'win32') {
    throw new Error('The Codex runtime probe currently supports Windows only')
  }

  switch (process.arch) {
    case 'x64':
      return {
        packageName: '@openai/codex-win32-x64',
        versionSuffix: 'win32-x64',
        targetTriple: 'x86_64-pc-windows-msvc',
      }
    case 'arm64':
      return {
        packageName: '@openai/codex-win32-arm64',
        versionSuffix: 'win32-arm64',
        targetTriple: 'aarch64-pc-windows-msvc',
      }
    default:
      throw new Error(`Unsupported Windows architecture: ${process.arch}`)
  }
}

async function getDirectorySize(path: string): Promise<number> {
  let size = 0
  for (const entry of await readdir(path, { withFileTypes: true })) {
    const entryPath = join(path, entry.name)
    size += entry.isDirectory()
      ? await getDirectorySize(entryPath)
      : (await stat(entryPath)).size
  }
  return size
}

function createRPCClient(child: ChildProcessWithoutNullStreams) {
  const pendingRequests = new Map<number, IPendingRequest>()
  const stderrLines = new Array<string>()
  const lines = createInterface({ input: child.stdout })

  child.stderr.setEncoding('utf8')
  child.stderr.on('data', chunk => {
    stderrLines.push(...String(chunk).split(/\r?\n/).filter(Boolean))
  })

  lines.on('line', line => {
    let message: unknown
    try {
      message = JSON.parse(line)
    } catch {
      return
    }

    if (!isRecord(message) || typeof message.id !== 'number') {
      return
    }

    const pending = pendingRequests.get(message.id)
    if (pending === undefined) {
      return
    }

    clearTimeout(pending.timeout)
    pendingRequests.delete(message.id)
    pending.resolve({
      id: message.id,
      result: message.result,
      error: message.error,
    })
  })

  child.on('exit', code => {
    const stderr = stderrLines.slice(-10).join('\n')
    const error = new Error(
      `Codex App Server exited before the probe completed (code ${code})${
        stderr.length > 0 ? `:\n${stderr}` : ''
      }`
    )
    for (const pending of pendingRequests.values()) {
      clearTimeout(pending.timeout)
      pending.reject(error)
    }
    pendingRequests.clear()
  })

  const request = (id: number, method: string, params: unknown) => {
    const response = new Promise<IRPCResponse>((resolve, reject) => {
      const timeout = setTimeout(() => {
        pendingRequests.delete(id)
        reject(new Error(`Timed out waiting for ${method}`))
      }, requestTimeoutMs)
      pendingRequests.set(id, { resolve, reject, timeout })
    })

    child.stdin.write(`${JSON.stringify({ id, method, params })}\n`)
    return response
  }

  const notify = (method: string) => {
    child.stdin.write(`${JSON.stringify({ method })}\n`)
  }

  return { notify, request }
}

function assertSuccessfulResponse(
  method: string,
  response: IRPCResponse
): Readonly<Record<string, unknown>> {
  if (response.error !== undefined) {
    throw new Error(`${method} returned an error`)
  }
  if (!isRecord(response.result)) {
    throw new Error(`${method} returned an invalid result`)
  }
  return response.result
}

async function stopChild(child: ChildProcessWithoutNullStreams) {
  if (child.exitCode !== null) {
    return
  }

  const exited = new Promise<void>(resolve => {
    child.once('exit', () => resolve())
  })
  child.kill()

  let timeout: NodeJS.Timeout | undefined
  const timedOut = new Promise<void>((_, reject) => {
    timeout = setTimeout(
      () => reject(new Error('Timed out stopping Codex App Server')),
      5_000
    )
  })

  try {
    await Promise.race([exited, timedOut])
  } finally {
    if (timeout !== undefined) {
      clearTimeout(timeout)
    }
  }
}

async function main() {
  const appPackage = await readPackageJSON(join(appRoot, 'package.json'))
  const pinnedVersion = appPackage.dependencies?.['@openai/codex']
  if (pinnedVersion === undefined || !/^\d+\.\d+\.\d+$/.test(pinnedVersion)) {
    throw new Error('@openai/codex must be pinned to an exact version')
  }

  const installedPackage = await readPackageJSON(
    appRequire.resolve('@openai/codex/package.json')
  )
  if (installedPackage.version !== pinnedVersion) {
    throw new Error(
      `Installed Codex ${installedPackage.version} does not match ${pinnedVersion}`
    )
  }

  const target = getWindowsTarget()
  const platformPackagePath = appRequire.resolve(
    `${target.packageName}/package.json`
  )
  const platformPackage = await readPackageJSON(platformPackagePath)
  const expectedPlatformVersion = `${pinnedVersion}-${target.versionSuffix}`
  if (platformPackage.version !== expectedPlatformVersion) {
    throw new Error(
      `Installed Codex runtime ${platformPackage.version} does not match ${expectedPlatformVersion}`
    )
  }

  const sourceTargetRoot = join(
    dirname(platformPackagePath),
    'vendor',
    target.targetTriple
  )
  const temporaryRoot = await mkdtemp(join(tmpdir(), 'wingit-codex-runtime-'))
  const resolvedTemporaryRoot = resolve(temporaryRoot)
  const resolvedTempDirectory = `${resolve(tmpdir())}${sep}`
  if (!resolvedTemporaryRoot.startsWith(resolvedTempDirectory)) {
    throw new Error('Refusing to use an unexpected temporary directory')
  }

  let child: ChildProcessWithoutNullStreams | undefined
  try {
    const packagedTargetRoot = join(
      temporaryRoot,
      'runtime',
      'vendor',
      target.targetTriple
    )
    const codexHome = join(temporaryRoot, 'codex-home')
    await cp(sourceTargetRoot, packagedTargetRoot, { recursive: true })
    await mkdir(codexHome)

    const executablePath = join(packagedTargetRoot, 'bin', 'codex.exe')
    const childEnvironment: NodeJS.ProcessEnv = {
      ...process.env,
      CODEX_HOME: codexHome,
      RUST_LOG: 'error',
    }
    for (const credentialName of [
      'CHATGPT_ACCESS_TOKEN',
      'CODEX_ACCESS_TOKEN',
      'OPENAI_API_KEY',
      'OPENAI_ORG_ID',
      'OPENAI_PROJECT_ID',
    ]) {
      delete childEnvironment[credentialName]
    }

    child = spawn(executablePath, ['app-server', '--stdio'], {
      cwd: join(temporaryRoot, 'runtime'),
      env: childEnvironment,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    })

    const rpc = createRPCClient(child)
    const initializeResult = assertSuccessfulResponse(
      'initialize',
      await rpc.request(1, 'initialize', {
        clientInfo: {
          name: 'wingit-runtime-probe',
          title: 'WinGit Runtime Probe',
          version: '1.0.0',
        },
        capabilities: { experimentalApi: false },
      })
    )
    rpc.notify('initialized')

    const accountResult = assertSuccessfulResponse(
      'account/read',
      await rpc.request(2, 'account/read', { refreshToken: false })
    )
    if (
      !('account' in accountResult) ||
      typeof accountResult.requiresOpenaiAuth !== 'boolean'
    ) {
      throw new Error('account/read omitted required account state')
    }

    const packagedSizeBytes = await getDirectorySize(
      join(temporaryRoot, 'runtime')
    )
    console.log(
      JSON.stringify(
        {
          package: '@openai/codex',
          version: pinnedVersion,
          platformPackage: target.packageName,
          platformVersion: platformPackage.version,
          architecture: process.arch,
          packagedSizeBytes,
          initialized: Object.keys(initializeResult).length > 0,
          accountRead: true,
          accountConfigured: accountResult.account !== null,
          requiresOpenaiAuth: accountResult.requiresOpenaiAuth,
        },
        null,
        2
      )
    )
  } finally {
    if (child !== undefined) {
      await stopChild(child)
    }
    await rm(temporaryRoot, { recursive: true, force: true })
  }
}

main().catch(error => {
  console.error(error instanceof Error ? error.message : String(error))
  process.exit(1)
})
