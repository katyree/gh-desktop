import * as React from 'react'

import type { ICodexAccountStoreState } from '../../lib/stores/codex-account-store'
import { assertNever } from '../../lib/fatal-error'
import { Button } from '../lib/button'
import { DialogContent, DialogPreferredFocusClassName } from '../dialog'

interface ICodexPreferencesProps {
  readonly state: ICodexAccountStoreState
  readonly onSignIn: () => void
  readonly onDeviceCodeSignIn: () => void
  readonly onCancelSignIn: () => void
  readonly onSignOut: () => void
  readonly onRetry: () => void
  readonly onResetPrivacyAcknowledgements: () => void
}

/** ChatGPT account settings for Codex-backed WinGit features. */
export class CodexPreferences extends React.Component<ICodexPreferencesProps> {
  public render() {
    return (
      <DialogContent className="codex-tab">
        <div className="codex-tab-content">
          <h2>ChatGPT account</h2>
          {this.renderAccountState()}
          {this.props.state.account.status === 'signed-in' &&
            this.renderRateLimits()}
          {this.props.state.account.status === 'signed-in' &&
            this.renderPrivacyControls()}
        </div>
      </DialogContent>
    )
  }

  private renderAccountState(): JSX.Element {
    const { account, login } = this.props.state

    switch (account.status) {
      case 'loading':
        return (
          <div className="codex-account-state" role="status">
            <p>Checking your ChatGPT account…</p>
          </div>
        )
      case 'signed-out':
        return (
          <div className="codex-account-state">
            <p>
              Sign in with the ChatGPT account for your Codex subscription.
              WinGit does not store your ChatGPT credentials.
            </p>
            <div className="codex-account-actions">
              <Button
                className={DialogPreferredFocusClassName}
                onClick={this.props.onSignIn}
              >
                Sign in with ChatGPT
              </Button>
              <Button onClick={this.props.onDeviceCodeSignIn}>
                Use a device code
              </Button>
            </div>
          </div>
        )
      case 'signing-in':
        return (
          <div className="codex-account-state" aria-live="polite">
            <p>
              {login?.method === 'device-code'
                ? 'Enter this one-time code in the browser window:'
                : 'Complete sign-in in your browser.'}
            </p>
            {login?.method === 'device-code' && login.userCode !== undefined && (
              <output
                className="codex-device-code"
                aria-label={`Device code ${login.userCode}`}
              >
                {login.userCode}
              </output>
            )}
            <div className="codex-account-actions">
              <Button
                className={DialogPreferredFocusClassName}
                onClick={this.props.onCancelSignIn}
              >
                Cancel sign-in
              </Button>
            </div>
          </div>
        )
      case 'signed-in':
        return (
          <div className="codex-account-state">
            <dl className="codex-account-details">
              <div>
                <dt>Account</dt>
                <dd>{account.email ?? 'ChatGPT account'}</dd>
              </div>
              <div>
                <dt>Plan</dt>
                <dd>{account.planType ?? 'Codex subscription'}</dd>
              </div>
            </dl>
            <div className="codex-account-actions">
              <Button
                className={DialogPreferredFocusClassName}
                onClick={this.props.onSignOut}
              >
                Sign out of ChatGPT
              </Button>
            </div>
          </div>
        )
      case 'expired':
        return this.renderRetryState(
          'Your ChatGPT session expired. Sign in again to use Codex features.',
          'Sign in again',
          this.props.onSignIn
        )
      case 'rate-limited':
        return this.renderRetryState(
          account.errorMessage ??
            'ChatGPT is temporarily rate limited. Try again shortly.',
          'Retry',
          this.props.onRetry
        )
      case 'error':
        return this.renderRetryState(
          account.errorMessage ??
            'WinGit could not read your ChatGPT account. Try again.',
          'Retry',
          this.props.onRetry
        )
      default:
        return assertNever(account.status, 'Unknown Codex account status')
    }
  }

  private renderRetryState(
    message: string,
    actionTitle: string,
    onAction: () => void
  ): JSX.Element {
    return (
      <div className="codex-account-state" role="alert">
        <p>{message}</p>
        <div className="codex-account-actions">
          <Button className={DialogPreferredFocusClassName} onClick={onAction}>
            {actionTitle}
          </Button>
        </div>
      </div>
    )
  }

  private renderRateLimits(): JSX.Element {
    const { rateLimits } = this.props.state
    const usedPercent = Math.max(
      rateLimits.primary?.usedPercent ?? 0,
      rateLimits.secondary?.usedPercent ?? 0
    )
    const reset =
      rateLimits.resetsAt === null ? null : (
        <>
          {' '}
          Resets{' '}
          <time dateTime={rateLimits.resetsAt}>
            {new Date(rateLimits.resetsAt).toLocaleString()}
          </time>
          .
        </>
      )

    let description: JSX.Element
    switch (rateLimits.status) {
      case 'available':
        description = <p>Codex generation is available.</p>
        break
      case 'near-limit':
        description = (
          <p>
            Codex is near its subscription limit ({Math.round(usedPercent)}%
            used).{reset}
          </p>
        )
        break
      case 'exhausted':
        description = <p>Codex subscription limit reached.{reset}</p>
        break
      case 'unavailable':
        description = <p>Subscription usage is unavailable.</p>
        break
      default:
        description = assertNever(
          rateLimits.status,
          'Unknown Codex rate-limit status'
        )
    }

    return (
      <section
        className="codex-rate-limits"
        aria-labelledby="codex-usage-title"
      >
        <h2 id="codex-usage-title">Subscription usage</h2>
        {description}
      </section>
    )
  }

  private renderPrivacyControls(): JSX.Element {
    return (
      <section className="codex-privacy" aria-labelledby="codex-privacy-title">
        <h2 id="codex-privacy-title">Repository privacy</h2>
        <p>
          WinGit asks before each repository first sends selected changes to
          OpenAI. Resetting makes WinGit ask again for every repository.
        </p>
        <Button onClick={this.props.onResetPrivacyAcknowledgements}>
          Reset privacy acknowledgements
        </Button>
      </section>
    )
  }
}
