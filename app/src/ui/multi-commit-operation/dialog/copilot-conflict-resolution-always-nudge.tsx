import * as React from 'react'
import { Dialog, DialogContent, DialogFooter } from '../../dialog'
import { OkCancelButtonGroup } from '../../dialog/ok-cancel-button-group'

interface ICopilotConflictResolutionAlwaysNudgeProps {
  readonly onAlwaysUseCopilot: () => void
  readonly onDecline: () => void
  readonly onDismissed: () => void
}

/**
 * Dialog nudging the user to enable the "Always use Copilot when conflicts are
 * detected" setting after they've used Copilot conflict resolution multiple
 * times in a row.
 */
export class CopilotConflictResolutionAlwaysNudge extends React.Component<ICopilotConflictResolutionAlwaysNudgeProps> {
  private onYes = () => {
    this.props.onAlwaysUseCopilot()
  }

  private onNo = (e: React.MouseEvent<HTMLButtonElement>) => {
    e.preventDefault()
    this.props.onDecline()
  }

  public render() {
    return (
      <Dialog
        id="copilot-conflict-resolution-always-nudge"
        title={
          __DARWIN__
            ? 'Always Use ChatGPT for Conflict Suggestions?'
            : 'Always use ChatGPT for conflict suggestions?'
        }
        onSubmit={this.onYes}
        onDismissed={this.props.onDismissed}
      >
        <DialogContent>
          <p>
            Would you like to automatically start ChatGPT suggestions whenever
            conflicts are detected? You can change this anytime in{' '}
            {__DARWIN__ ? 'Settings → Codex' : 'File → Options → Codex'}.
          </p>
        </DialogContent>
        <DialogFooter>
          <OkCancelButtonGroup
            okButtonText="Yes"
            cancelButtonText="No"
            onCancelButtonClick={this.onNo}
          />
        </DialogFooter>
      </Dialog>
    )
  }
}
