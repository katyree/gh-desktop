import {
  CodexLoginMethod,
  ICodexAccountState,
  ICodexLoginStart,
  ICodexModel,
  ICodexRateLimitState,
  CodexModelsState,
} from '../codex-ipc'
import {
  cancelCodexAccountLogin,
  logoutCodexAccount,
  onCodexAccountStateChanged,
  onCodexRateLimitsStateChanged,
  readCodexAccount,
  readCodexModels,
  readCodexRateLimits,
  startCodexAccountLogin,
} from '../../ui/main-process-proxy'
import { TypedBaseStore } from './base-store'

export interface ICodexAccountStoreState {
  readonly account: ICodexAccountState
  readonly login: ICodexLoginStart | null
  readonly rateLimits: ICodexRateLimitState
  /** The account-scoped model catalog. */
  readonly models: CodexModelsState
}

export type CodexCommitMessageAvailability =
  | 'ready'
  | 'account-required'
  | 'rate-limit-exhausted'

/** The complete eligibility rule for Codex commit-message generation. */
export function getCodexCommitMessageAvailability(
  state: ICodexAccountStoreState
): CodexCommitMessageAvailability {
  if (state.account.status !== 'signed-in') {
    return 'account-required'
  }
  return state.rateLimits.status === 'exhausted'
    ? 'rate-limit-exhausted'
    : 'ready'
}

interface ICodexAccountIPC {
  readonly readAccount: (refreshToken: boolean) => Promise<ICodexAccountState>
  readonly startLogin: (method: CodexLoginMethod) => Promise<ICodexLoginStart>
  readonly cancelLogin: (loginId: string) => Promise<void>
  readonly logout: () => Promise<ICodexAccountState>
  readonly readRateLimits: () => Promise<ICodexRateLimitState>
  readonly readModels: () => Promise<ReadonlyArray<ICodexModel>>
  readonly onStateChanged: (
    handler: (state: ICodexAccountState) => void
  ) => () => void
  readonly onRateLimitsChanged: (
    handler: (state: ICodexRateLimitState) => void
  ) => () => void
}

const signedOutState: ICodexAccountState = {
  status: 'signed-out',
  type: 'signed-out',
  email: null,
  planType: null,
  requiresOpenaiAuth: true,
}

const defaultIPC: ICodexAccountIPC = {
  readAccount: readCodexAccount,
  startLogin: startCodexAccountLogin,
  cancelLogin: cancelCodexAccountLogin,
  logout: logoutCodexAccount,
  readRateLimits: readCodexRateLimits,
  readModels: readCodexModels,
  onStateChanged: handler => onCodexAccountStateChanged(handler),
  onRateLimitsChanged: handler => onCodexRateLimitsStateChanged(handler),
}

const unavailableRateLimits: ICodexRateLimitState = {
  status: 'unavailable',
  primary: null,
  secondary: null,
  resetsAt: null,
}

function errorState(message: string): ICodexAccountState {
  return {
    status: 'error',
    type: 'signed-out',
    email: null,
    planType: null,
    requiresOpenaiAuth: true,
    errorMessage: message,
  }
}

/**
 * Renderer-only view state for the App Server-managed ChatGPT account. No part
 * of this store is written to localStorage; Codex owns credential persistence.
 */
export class CodexAccountStore extends TypedBaseStore<ICodexAccountStoreState> {
  private state: ICodexAccountStoreState = {
    account: { ...signedOutState, status: 'loading' },
    login: null,
    rateLimits: unavailableRateLimits,
    models: { kind: 'loading' },
  }
  private initializePromise: Promise<void> | undefined
  private readonly removeStateListener: () => void
  private readonly removeRateLimitsListener: () => void

  public constructor(private readonly ipc: ICodexAccountIPC = defaultIPC) {
    super()
    this.removeStateListener = ipc.onStateChanged(this.handleAccountState)
    this.removeRateLimitsListener = ipc.onRateLimitsChanged(
      this.handleRateLimitsState
    )
  }

  public get currentState(): ICodexAccountStoreState {
    return this.state
  }

  public initialize(): Promise<void> {
    if (this.initializePromise !== undefined) {
      return this.initializePromise
    }
    const promise = Promise.all([
      this.refresh(false),
      this.refreshRateLimits(),
      this.refreshModels(),
    ]).then(() => undefined)
    this.initializePromise = promise
    return promise
  }

  public async refresh(refreshToken = false): Promise<void> {
    try {
      this.update({ account: await this.ipc.readAccount(refreshToken) })
    } catch {
      this.update({
        account: errorState(
          'WinGit could not read your ChatGPT account. Try again.'
        ),
      })
    }
  }

  public async refreshRateLimits(): Promise<void> {
    try {
      this.update({ rateLimits: await this.ipc.readRateLimits() })
    } catch {
      this.update({ rateLimits: unavailableRateLimits })
    }
  }

  public async refreshModels(): Promise<void> {
    this.update({ models: { kind: 'loading' } })
    try {
      this.update({
        models: { kind: 'ready', models: await this.ipc.readModels() },
      })
    } catch {
      this.update({ models: { kind: 'unavailable' } })
    }
  }

  public async startLogin(method: CodexLoginMethod): Promise<void> {
    if (method !== 'browser' && method !== 'device-code') {
      throw new Error('Unsupported Codex login method')
    }

    this.update({
      account: {
        ...this.state.account,
        status: 'signing-in',
        errorMessage: undefined,
      },
      login: null,
    })

    try {
      const login = await this.ipc.startLogin(method)
      this.update({ login })
    } catch {
      this.update({
        account: errorState(
          'WinGit could not start ChatGPT sign-in. Try again.'
        ),
        login: null,
      })
    }
  }

  public async cancelLogin(): Promise<void> {
    const login = this.state.login
    if (login === null) {
      return
    }

    try {
      await this.ipc.cancelLogin(login.loginId)
      this.update({ account: signedOutState, login: null })
    } catch {
      this.update({
        account: errorState(
          'WinGit could not cancel ChatGPT sign-in. Try again.'
        ),
      })
    }
  }

  public async logout(): Promise<void> {
    this.update({
      account: { ...this.state.account, status: 'loading' },
      login: null,
    })
    try {
      this.update({
        account: await this.ipc.logout(),
        login: null,
        rateLimits: unavailableRateLimits,
      })
    } catch {
      this.update({
        account: errorState('WinGit could not sign out of ChatGPT. Try again.'),
      })
    }
  }

  public dispose() {
    this.removeStateListener()
    this.removeRateLimitsListener()
  }

  private readonly handleAccountState = (account: ICodexAccountState) => {
    this.update({
      account,
      login: null,
      ...(account.status === 'signed-in'
        ? {}
        : { rateLimits: unavailableRateLimits }),
    })
    if (account.status === 'signed-in') {
      void this.refreshModels()
    }
  }

  private readonly handleRateLimitsState = (
    rateLimits: ICodexRateLimitState
  ) => {
    this.update({ rateLimits })
  }

  private update(update: Partial<ICodexAccountStoreState>) {
    this.state = { ...this.state, ...update }
    this.emitUpdate(this.state)
  }
}
