import assert from 'node:assert'
import { describe, it } from 'node:test'
import {
  CodexLoginMethod,
  ICodexAccountState,
  ICodexRateLimitState,
} from '../../../src/lib/codex-ipc'
import {
  CodexAccountStore,
  getCodexCommitMessageAvailability,
} from '../../../src/lib/stores/codex-account-store'

const signedOut: ICodexAccountState = {
  status: 'signed-out',
  type: 'signed-out',
  email: null,
  planType: null,
  requiresOpenaiAuth: true,
}

const signedIn: ICodexAccountState = {
  status: 'signed-in',
  type: 'chatgpt',
  email: 'test.user@example.invalid',
  planType: 'plus',
  requiresOpenaiAuth: false,
}

const availableRateLimits: ICodexRateLimitState = {
  status: 'available',
  primary: { usedPercent: 20, resetsAt: '2026-09-01T00:00:00.000Z' },
  secondary: null,
  resetsAt: '2026-09-01T00:00:00.000Z',
}

function createAccountIPC(initialState = signedOut) {
  let stateListener: ((state: ICodexAccountState) => void) | undefined
  let rateLimitsListener: ((state: ICodexRateLimitState) => void) | undefined
  const calls = new Array<string>()
  const ipc = {
    readAccount: async (refreshToken: boolean) => {
      calls.push(`read:${String(refreshToken)}`)
      return initialState
    },
    startLogin: async (method: CodexLoginMethod) => {
      calls.push(`start:${method}`)
      return method === 'browser'
        ? { method, loginId: 'login-browser' }
        : {
            method,
            loginId: 'login-device',
            userCode: 'TEST-CODE',
          }
    },
    cancelLogin: async (loginId: string) => {
      calls.push(`cancel:${loginId}`)
    },
    logout: async () => {
      calls.push('logout')
      return signedOut
    },
    readRateLimits: async () => {
      calls.push('read-rate-limits')
      return availableRateLimits
    },
    onStateChanged: (listener: (state: ICodexAccountState) => void) => {
      stateListener = listener
      return () => {
        stateListener = undefined
      }
    },
    onRateLimitsChanged: (listener: (state: ICodexRateLimitState) => void) => {
      rateLimitsListener = listener
      return () => {
        rateLimitsListener = undefined
      }
    },
  }
  return {
    calls,
    emit: (state: ICodexAccountState) => stateListener?.(state),
    emitRateLimits: (state: ICodexRateLimitState) =>
      rateLimitsListener?.(state),
    ipc,
  }
}

describe('CodexAccountStore', () => {
  it('gates generation only on ChatGPT state and explicit exhaustion', () => {
    const unavailable: ICodexRateLimitState = {
      status: 'unavailable',
      primary: null,
      secondary: null,
      resetsAt: null,
    }
    const exhausted: ICodexRateLimitState = {
      status: 'exhausted',
      primary: { usedPercent: 100, resetsAt: null },
      secondary: null,
      resetsAt: null,
    }

    assert.equal(
      getCodexCommitMessageAvailability({
        account: signedOut,
        login: null,
        rateLimits: availableRateLimits,
      }),
      'account-required'
    )
    assert.equal(
      getCodexCommitMessageAvailability({
        account: signedIn,
        login: null,
        rateLimits: unavailable,
      }),
      'ready'
    )
    assert.equal(
      getCodexCommitMessageAvailability({
        account: signedIn,
        login: null,
        rateLimits: exhausted,
      }),
      'rate-limit-exhausted'
    )
  })

  it('restores App Server-managed account state on initialization', async () => {
    const accountIPC = createAccountIPC(signedIn)
    const store = new CodexAccountStore(accountIPC.ipc)

    assert.equal(store.currentState.account.status, 'loading')
    await store.initialize()

    assert.deepStrictEqual(store.currentState, {
      account: signedIn,
      login: null,
      rateLimits: availableRateLimits,
    })
    assert.deepStrictEqual(accountIPC.calls, ['read:false', 'read-rate-limits'])
    store.dispose()
  })

  it('models browser login and completion independently of GitHub state', async () => {
    const accountIPC = createAccountIPC()
    const store = new CodexAccountStore(accountIPC.ipc)

    await store.startLogin('browser')
    assert.equal(store.currentState.account.status, 'signing-in')
    assert.deepStrictEqual(store.currentState.login, {
      method: 'browser',
      loginId: 'login-browser',
    })

    accountIPC.emit(signedIn)
    assert.deepStrictEqual(store.currentState, {
      account: signedIn,
      login: null,
      rateLimits: {
        status: 'unavailable',
        primary: null,
        secondary: null,
        resetsAt: null,
      },
    })
    assert.deepStrictEqual(accountIPC.calls, ['start:browser'])
    store.dispose()
  })

  it('keeps a device code in memory and cancels the matching login', async () => {
    const accountIPC = createAccountIPC()
    const store = new CodexAccountStore(accountIPC.ipc)

    await store.startLogin('device-code')
    assert.deepStrictEqual(store.currentState.login, {
      method: 'device-code',
      loginId: 'login-device',
      userCode: 'TEST-CODE',
    })

    await store.cancelLogin()
    assert.deepStrictEqual(store.currentState, {
      account: signedOut,
      login: null,
      rateLimits: {
        status: 'unavailable',
        primary: null,
        secondary: null,
        resetsAt: null,
      },
    })
    assert.deepStrictEqual(accountIPC.calls, [
      'start:device-code',
      'cancel:login-device',
    ])
    store.dispose()
  })

  it('logs out only the Codex account', async () => {
    const accountIPC = createAccountIPC(signedIn)
    const store = new CodexAccountStore(accountIPC.ipc)
    await store.initialize()

    await store.logout()

    assert.deepStrictEqual(store.currentState.account, signedOut)
    assert.deepStrictEqual(accountIPC.calls, [
      'read:false',
      'read-rate-limits',
      'logout',
    ])
    store.dispose()
  })

  it('updates subscription usage from App Server notifications', () => {
    const accountIPC = createAccountIPC(signedIn)
    const store = new CodexAccountStore(accountIPC.ipc)
    const exhausted: ICodexRateLimitState = {
      status: 'exhausted',
      primary: {
        usedPercent: 100,
        resetsAt: '2026-09-01T00:00:00.000Z',
      },
      secondary: null,
      resetsAt: '2026-09-01T00:00:00.000Z',
    }

    accountIPC.emitRateLimits(exhausted)

    assert.deepStrictEqual(store.currentState.rateLimits, exhausted)
    store.dispose()
  })

  it('maps login failures to a safe retryable state', async () => {
    const accountIPC = createAccountIPC()
    accountIPC.ipc.startLogin = async () => {
      throw new Error('sensitive upstream detail')
    }
    const store = new CodexAccountStore(accountIPC.ipc)

    await store.startLogin('browser')

    assert.equal(store.currentState.account.status, 'error')
    assert.equal(
      store.currentState.account.errorMessage,
      'WinGit could not start ChatGPT sign-in. Try again.'
    )
    assert(!JSON.stringify(store.currentState).includes('sensitive'))
    store.dispose()
  })
})
