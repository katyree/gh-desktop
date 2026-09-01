import assert from 'node:assert'
import { describe, it } from 'node:test'
import * as React from 'react'

import type { CodexAccountStatus } from '../../../src/lib/codex-ipc'
import type { ICodexAccountStoreState } from '../../../src/lib/stores/codex-account-store'
import { CodexPreferences } from '../../../src/ui/preferences/codex'
import { fireEvent, render, screen } from '../../helpers/ui/render'

interface IActionCalls {
  signIn: number
  deviceCode: number
  cancel: number
  signOut: number
  retry: number
  resetPrivacy: number
}

function state(
  status: CodexAccountStatus,
  overrides: Partial<ICodexAccountStoreState> = {}
): ICodexAccountStoreState {
  return {
    account: {
      status,
      type: status === 'signed-in' ? 'chatgpt' : 'signed-out',
      email: status === 'signed-in' ? 'test.user@example.com' : null,
      planType: status === 'signed-in' ? 'Plus' : null,
      requiresOpenaiAuth: status !== 'signed-in',
    },
    login: null,
    rateLimits: {
      status: 'unavailable',
      primary: null,
      secondary: null,
      resetsAt: null,
    },
    ...overrides,
  }
}

function renderPreferences(
  accountState: ICodexAccountStoreState
): IActionCalls {
  const calls: IActionCalls = {
    signIn: 0,
    deviceCode: 0,
    cancel: 0,
    signOut: 0,
    retry: 0,
    resetPrivacy: 0,
  }

  render(
    <CodexPreferences
      state={accountState}
      onSignIn={() => calls.signIn++}
      onDeviceCodeSignIn={() => calls.deviceCode++}
      onCancelSignIn={() => calls.cancel++}
      onSignOut={() => calls.signOut++}
      onRetry={() => calls.retry++}
      onResetPrivacyAcknowledgements={() => calls.resetPrivacy++}
    />
  )

  return calls
}

describe('CodexPreferences', () => {
  it('renders the loading state without an action', () => {
    renderPreferences(state('loading'))

    assert.ok(screen.getByRole('status'))
    assert.ok(screen.getByText('Checking your ChatGPT account…'))
    assert.equal(screen.queryByRole('button'), null)
  })

  it('offers browser and device-code sign-in without a Copilot license', () => {
    const calls = renderPreferences(state('signed-out'))
    const browserButton = screen.getByRole('button', {
      name: 'Sign in with ChatGPT',
    })
    const deviceButton = screen.getByRole('button', {
      name: 'Use a device code',
    })

    fireEvent.click(browserButton)
    fireEvent.click(deviceButton)

    assert.equal(calls.signIn, 1)
    assert.equal(calls.deviceCode, 1)
    assert.equal(document.body.textContent?.includes('Copilot license'), false)
  })

  it('renders browser sign-in with one cancel action', () => {
    const calls = renderPreferences(
      state('signing-in', {
        login: { method: 'browser', loginId: 'login-1' },
      })
    )

    assert.ok(screen.getByText('Complete sign-in in your browser.'))
    const cancelButton = screen.getByRole('button', { name: 'Cancel sign-in' })
    fireEvent.click(cancelButton)
    assert.equal(calls.cancel, 1)
  })

  it('announces the device code and offers one cancel action', () => {
    const calls = renderPreferences(
      state('signing-in', {
        login: {
          method: 'device-code',
          loginId: 'login-2',
          userCode: 'TEST-CODE',
        },
      })
    )

    assert.ok(screen.getByLabelText('Device code TEST-CODE'))
    const cancelButton = screen.getByRole('button', { name: 'Cancel sign-in' })
    fireEvent.click(cancelButton)
    assert.equal(calls.cancel, 1)
  })

  it('shows the signed-in account and sign-out action', () => {
    const calls = renderPreferences(state('signed-in'))

    assert.ok(screen.getByText('test.user@example.com'))
    assert.ok(screen.getByText('Plus'))
    const signOutButton = screen.getByRole('button', {
      name: 'Sign out of ChatGPT',
    })
    fireEvent.click(signOutButton)
    assert.equal(calls.signOut, 1)
  })

  it('offers a clear privacy acknowledgement reset', () => {
    const calls = renderPreferences(state('signed-in'))
    const reset = screen.getByRole('button', {
      name: 'Reset privacy acknowledgements',
    })

    fireEvent.click(reset)
    assert.equal(calls.resetPrivacy, 1)
  })

  it('shows available and unavailable subscription usage without billing labels', () => {
    const view = render(
      <CodexPreferences
        state={{
          ...state('signed-in'),
          rateLimits: {
            status: 'available',
            primary: { usedPercent: 20, resetsAt: null },
            secondary: null,
            resetsAt: null,
          },
        }}
        onSignIn={() => undefined}
        onDeviceCodeSignIn={() => undefined}
        onCancelSignIn={() => undefined}
        onSignOut={() => undefined}
        onRetry={() => undefined}
        onResetPrivacyAcknowledgements={() => undefined}
      />
    )

    assert.ok(screen.getByText('Codex generation is available.'))
    assert.equal(view.container.textContent?.includes('API charges'), false)

    view.rerender(
      <CodexPreferences
        state={state('signed-in')}
        onSignIn={() => undefined}
        onDeviceCodeSignIn={() => undefined}
        onCancelSignIn={() => undefined}
        onSignOut={() => undefined}
        onRetry={() => undefined}
        onResetPrivacyAcknowledgements={() => undefined}
      />
    )
    assert.ok(screen.getByText('Subscription usage is unavailable.'))
  })

  it('shows near-limit percentage and exhausted reset time', () => {
    const sharedProps = {
      onSignIn: () => undefined,
      onDeviceCodeSignIn: () => undefined,
      onCancelSignIn: () => undefined,
      onSignOut: () => undefined,
      onRetry: () => undefined,
      onResetPrivacyAcknowledgements: () => undefined,
    }
    const view = render(
      <CodexPreferences
        state={{
          ...state('signed-in'),
          rateLimits: {
            status: 'near-limit',
            primary: { usedPercent: 84.7, resetsAt: null },
            secondary: null,
            resetsAt: null,
          },
        }}
        {...sharedProps}
      />
    )
    assert.ok(screen.getByText(/near its subscription limit \(85% used\)/))

    const resetsAt = '2026-09-01T00:00:00.000Z'
    view.rerender(
      <CodexPreferences
        state={{
          ...state('signed-in'),
          rateLimits: {
            status: 'exhausted',
            primary: { usedPercent: 100, resetsAt },
            secondary: null,
            resetsAt,
          },
        }}
        {...sharedProps}
      />
    )
    assert.ok(screen.getByText(/Codex subscription limit reached/))
    assert.equal(view.container.querySelector('time')?.dateTime, resetsAt)
  })

  it('offers sign-in again for an expired session', () => {
    const calls = renderPreferences(state('expired'))
    const button = screen.getByRole('button', { name: 'Sign in again' })

    fireEvent.click(button)
    assert.equal(calls.signIn, 1)
  })

  it('offers retry for a rate-limited account read', () => {
    const calls = renderPreferences(state('rate-limited'))
    const button = screen.getByRole('button', { name: 'Retry' })

    fireEvent.click(button)
    assert.equal(calls.retry, 1)
  })

  it('shows a safe account error and offers retry', () => {
    const calls = renderPreferences({
      ...state('error'),
      account: {
        ...state('error').account,
        errorMessage: 'WinGit could not read your ChatGPT account. Try again.',
      },
    })
    const button = screen.getByRole('button', { name: 'Retry' })

    assert.ok(
      screen.getByText('WinGit could not read your ChatGPT account. Try again.')
    )
    fireEvent.click(button)
    assert.equal(calls.retry, 1)
  })
})
