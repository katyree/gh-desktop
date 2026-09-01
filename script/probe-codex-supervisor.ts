import { createRequire } from 'module'
import { mkdir, mkdtemp, rm } from 'fs/promises'
import { tmpdir } from 'os'
import { dirname, join } from 'path'
import { CodexAppServerSupervisor } from '../app/src/main-process/codex-app-server-supervisor'
import { CodexAppServerClient } from '../app/src/main-process/codex-app-server-client'

const projectRoot = dirname(__dirname)
const appRequire = createRequire(join(projectRoot, 'app', 'package.json'))

function getPlatformRuntime() {
  if (process.platform !== 'win32') {
    throw new Error(
      'The Codex supervisor probe currently supports Windows only'
    )
  }

  switch (process.arch) {
    case 'x64':
      return {
        packageName: '@openai/codex-win32-x64',
        targetTriple: 'x86_64-pc-windows-msvc',
      }
    case 'arm64':
      return {
        packageName: '@openai/codex-win32-arm64',
        targetTriple: 'aarch64-pc-windows-msvc',
      }
    default:
      throw new Error(`Unsupported Windows architecture: ${process.arch}`)
  }
}

function isProcessRunning(pid: number) {
  try {
    process.kill(pid, 0)
    return true
  } catch (error) {
    if (error instanceof Error && 'code' in error && error.code === 'ESRCH') {
      return false
    }
    throw error
  }
}

async function main() {
  const platformRuntime = getPlatformRuntime()
  const platformPackagePath = appRequire.resolve(
    `${platformRuntime.packageName}/package.json`
  )
  const platformPackageRoot = dirname(platformPackagePath)
  const temporaryRoot = await mkdtemp(
    join(tmpdir(), 'wingit-codex-supervisor-')
  )
  const supervisor = new CodexAppServerSupervisor({
    executablePath: join(
      platformPackageRoot,
      'vendor',
      platformRuntime.targetTriple,
      'bin',
      'codex.exe'
    ),
    codexHomePath: join(temporaryRoot, 'codex-home'),
  })
  const generationWorkingDirectory = join(temporaryRoot, 'empty-workspace')
  await mkdir(generationWorkingDirectory)
  const client = new CodexAppServerClient(
    supervisor,
    'supervisor-probe',
    generationWorkingDirectory,
    () => undefined
  )

  try {
    const firstStart = supervisor.start()
    const secondStart = supervisor.start()
    const [firstChild, secondChild] = await Promise.all([
      firstStart,
      secondStart,
    ])
    if (firstChild !== secondChild || firstChild.pid === undefined) {
      throw new Error('Concurrent starts did not share one Codex process')
    }

    const pid = firstChild.pid
    if (!isProcessRunning(pid)) {
      throw new Error('Codex App Server was not running after startup')
    }

    const account = await client.readAccount(false)
    await client.shutdown()
    if (isProcessRunning(pid)) {
      throw new Error(`Codex App Server process ${pid} survived shutdown`)
    }

    console.log(
      JSON.stringify(
        {
          architecture: process.arch,
          sharedProcess: true,
          startupState: 'running',
          accountRead: true,
          accountType: account.type,
          shutdownState: supervisor.snapshot.state,
          orphanedProcess: false,
        },
        null,
        2
      )
    )
  } finally {
    await client.shutdown()
    await rm(temporaryRoot, { recursive: true, force: true })
  }
}

main().catch(error => {
  console.error(error instanceof Error ? error.message : String(error))
  process.exit(1)
})
