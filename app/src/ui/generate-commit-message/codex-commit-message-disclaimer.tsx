import * as React from 'react'
import {
  Dialog,
  DialogContent,
  DialogFooter,
  OkCancelButtonGroup,
} from '../dialog'

interface ICodexCommitMessageDisclaimerProps {
  readonly onAccepted: () => void
  readonly onDismissed: () => void
}

/** Consent shown before a repository first sends commit context to OpenAI. */
export class CodexCommitMessageDisclaimer extends React.Component<ICodexCommitMessageDisclaimerProps> {
  public render() {
    return (
      <Dialog
        title="Share selected changes with OpenAI?"
        type="warning"
        onDismissed={this.props.onDismissed}
        onSubmit={this.onSubmit}
        ariaDescribedBy="codex-commit-consent-body"
        role="alertdialog"
      >
        <DialogContent>
          <div id="codex-commit-consent-body">
            <p>
              WinGit will send the diff for the files selected for this commit
              and any enforced commit-message rules to OpenAI through Codex.
            </p>
            <p>
              It will not send unselected files, repository history, remote
              URLs, environment variables, or Git credentials. Review and edit
              the result; WinGit will not create a commit automatically.
            </p>
          </div>
        </DialogContent>
        <DialogFooter>
          <OkCancelButtonGroup okButtonText="Share selected changes" />
        </DialogFooter>
      </Dialog>
    )
  }

  private onSubmit = () => {
    this.props.onAccepted()
    this.props.onDismissed()
  }
}
