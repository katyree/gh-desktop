import * as React from 'react'
import { DiffParser } from '../../lib/diff-parser'
import type { ISelectedChangesReviewFinding } from '../../lib/codex-selected-changes-review-generator'
import type { ISelectedChangesReviewSnapshot } from '../../lib/selected-changes-review-snapshot'
import type { SelectedChangesReviewState } from '../../lib/selected-changes-review-state'
import { DiffLine, DiffLineType } from '../../models/diff'
import { Dialog, DialogContent, DialogFooter } from '../dialog'
import { Button } from '../lib/button'
import { LinkButton } from '../lib/link-button'

export interface ISelectedChangesReviewDialogProps {
  readonly review: SelectedChangesReviewState
  readonly onDismissed: () => void
  readonly onReviewAgain: () => void
  readonly onCancel: () => void
  readonly canReview: boolean
  readonly disabledReason?: string
}

interface IReviewLocation {
  readonly path: string
  readonly line: number
  readonly side: 'old' | 'new'
}

interface ISelectedChangesReviewDialogState {
  readonly focusedLocation: IReviewLocation | null
}

interface IReviewDiffContext {
  readonly lines: ReadonlyArray<DiffLine>
  readonly hasLinesBefore: boolean
  readonly hasLinesAfter: boolean
}

const ReviewContextLineCount = 3

function isSameLocation(
  left: IReviewLocation | null,
  right: IReviewLocation | null
): boolean {
  return (
    left !== null &&
    right !== null &&
    left.path === right.path &&
    left.line === right.line &&
    left.side === right.side
  )
}

interface ISelectedChangesReviewLocationLinkProps {
  readonly finding: ISelectedChangesReviewFinding
  readonly onActivate: (finding: ISelectedChangesReviewFinding) => void
}

class SelectedChangesReviewLocationLink extends React.PureComponent<ISelectedChangesReviewLocationLinkProps> {
  private onClick = () => this.props.onActivate(this.props.finding)

  public render() {
    const { finding } = this.props
    return (
      <LinkButton
        className="selected-changes-review-location-link"
        ariaLabel={`Open reviewed diff for ${finding.path}, line ${finding.line}, ${finding.side} side`}
        onClick={this.onClick}
      >
        <code>
          {finding.path}:{finding.line}
        </code>
      </LinkButton>
    )
  }
}

/** Display the findings and the immutable diff that was reviewed. */
export class SelectedChangesReviewDialog extends React.Component<
  ISelectedChangesReviewDialogProps,
  ISelectedChangesReviewDialogState
> {
  private focusedLineElement: HTMLDivElement | null = null

  public constructor(props: ISelectedChangesReviewDialogProps) {
    super(props)
    this.state = { focusedLocation: null }
  }

  public componentDidUpdate(
    prevProps: ISelectedChangesReviewDialogProps,
    prevState: ISelectedChangesReviewDialogState
  ) {
    if (
      prevProps.review !== this.props.review &&
      this.state.focusedLocation !== null
    ) {
      this.setState({ focusedLocation: null })
      return
    }

    if (
      prevState.focusedLocation !== this.state.focusedLocation &&
      this.state.focusedLocation !== null &&
      this.focusedLineElement !== null
    ) {
      this.focusedLineElement.focus()
      this.focusedLineElement.scrollIntoView({ block: 'nearest' })
    }
  }

  public render() {
    const isPending = this.props.review.kind === 'pending'

    return (
      <Dialog
        id="selected-changes-review"
        title="Review selected changes"
        loading={isPending}
        dismissDisabled={isPending}
        onDismissed={this.props.onDismissed}
      >
        <DialogContent className="selected-changes-review-content">
          {this.renderContent()}
        </DialogContent>
        {this.renderFooter()}
      </Dialog>
    )
  }

  private renderContent(): React.ReactNode {
    const { review } = this.props

    switch (review.kind) {
      case 'idle':
        return (
          <p className="selected-changes-review-empty">
            Select changes to review them with Codex.
          </p>
        )
      case 'pending':
        return (
          <div
            className="selected-changes-review-pending"
            role="status"
            aria-live="polite"
          >
            <p>Reviewing the selected changes…</p>
            <p className="secondary-text">
              The review uses only the selected diff and does not change your
              commit.
            </p>
          </div>
        )
      case 'complete':
        return this.renderFindings(review.findings, review.snapshot)
      case 'outdated':
        return (
          <>
            <div
              className="selected-changes-review-outdated"
              role="status"
              aria-live="polite"
            >
              <strong>Outdated review</strong>
              <span>
                {review.reason === 'selection-changed'
                  ? ' The selected changes have changed.'
                  : ' The selected file contents have changed.'}{' '}
                Review again to check the current selection.
              </span>
            </div>
            {review.findings.length > 0 &&
              this.renderFindings(review.findings, review.snapshot)}
          </>
        )
      case 'error':
        return (
          <div className="selected-changes-review-error" role="alert">
            <p>{review.message}</p>
            <p className="secondary-text">
              Review again to request a new result for the selected changes.
            </p>
          </div>
        )
    }
  }

  private renderFindings(
    findings: ReadonlyArray<ISelectedChangesReviewFinding>,
    snapshot: ISelectedChangesReviewSnapshot | null
  ) {
    if (findings.length === 0) {
      return (
        <div className="selected-changes-review-empty" role="status">
          <p>No findings in the selected changes.</p>
          <p className="secondary-text">
            This review used only the selected diff. An empty result does not
            claim that the changes are safe.
          </p>
        </div>
      )
    }

    return (
      <ul
        className="selected-changes-review-findings"
        aria-label="Codex review findings"
      >
        {findings.map((finding, index) => (
          <li
            className="selected-changes-review-finding"
            key={`${finding.path}:${finding.line}:${finding.side}:${index}`}
          >
            <div className="selected-changes-review-finding-location">
              <SelectedChangesReviewLocationLink
                finding={finding}
                onActivate={this.focusFinding}
              />
              <span className="selected-changes-review-side">
                {finding.side === 'new' ? 'added line' : 'deleted line'}
              </span>
            </div>
            <h2>{finding.title}</h2>
            <p>{finding.explanation}</p>
            <p className="selected-changes-review-suggestion">
              <strong>Suggested correction:</strong> {finding.suggestion}
            </p>
            {this.renderDiffContext(finding, snapshot)}
          </li>
        ))}
      </ul>
    )
  }

  private renderDiffContext(
    finding: ISelectedChangesReviewFinding,
    snapshot: ISelectedChangesReviewSnapshot | null
  ) {
    if (!isSameLocation(this.state.focusedLocation, finding)) {
      return null
    }

    const context =
      snapshot === null ? null : this.findDiffContext(snapshot, finding)

    if (context === null) {
      return (
        <p className="selected-changes-review-context-missing" role="status">
          The reviewed snapshot line is unavailable.
        </p>
      )
    }

    return (
      <div
        className="selected-changes-review-diff-viewer"
        role="region"
        aria-label={`Reviewed diff context for ${finding.path} line ${finding.line}`}
      >
        <div
          className="selected-changes-review-diff-table"
          role="table"
          aria-label="Reviewed diff lines"
        >
          {context.hasLinesBefore && (
            <div className="selected-changes-review-diff-ellipsis" role="row">
              …
            </div>
          )}
          {context.lines.map((line, index) => {
            const isFocused = this.isFindingLine(line, finding)
            return (
              <div
                className={`selected-changes-review-diff-row ${
                  line.type === DiffLineType.Add
                    ? 'is-added'
                    : line.type === DiffLineType.Delete
                    ? 'is-deleted'
                    : ''
                }${isFocused ? ' is-focused' : ''}`}
                key={`${line.originalLineNumber ?? 'hunk'}:${index}`}
                role="row"
                tabIndex={isFocused ? -1 : undefined}
                aria-current={isFocused ? 'true' : undefined}
                aria-label={this.getDiffLineAriaLabel(line)}
                ref={isFocused ? this.setFocusedLineElement : undefined}
              >
                <span
                  className="selected-changes-review-diff-number"
                  role="cell"
                >
                  {line.oldLineNumber ?? ''}
                </span>
                <span
                  className="selected-changes-review-diff-number"
                  role="cell"
                >
                  {line.newLineNumber ?? ''}
                </span>
                <code className="selected-changes-review-diff-text" role="cell">
                  <span className="selected-changes-review-diff-marker">
                    {this.getDiffLineMarker(line)}
                  </span>
                  {line.type === DiffLineType.Hunk ? line.text : line.content}
                </code>
              </div>
            )
          })}
          {context.hasLinesAfter && (
            <div className="selected-changes-review-diff-ellipsis" role="row">
              …
            </div>
          )}
        </div>
      </div>
    )
  }

  private renderFooter() {
    const { review } = this.props

    if (review.kind === 'pending') {
      return (
        <DialogFooter>
          <div className="button-group">
            <Button
              ariaLabel="Cancel selected-changes review"
              onClick={this.props.onCancel}
            >
              Cancel review
            </Button>
          </div>
        </DialogFooter>
      )
    }

    const canRetry =
      review.kind === 'complete' ||
      review.kind === 'outdated' ||
      review.kind === 'error'

    return (
      <DialogFooter>
        <div className="button-group">
          {canRetry && (
            <Button
              className="selected-changes-review-again"
              disabled={!this.props.canReview}
              onClick={this.props.onReviewAgain}
              tooltip={this.props.disabledReason}
            >
              Review again
            </Button>
          )}
          <Button onClick={this.props.onDismissed}>Close</Button>
        </div>
      </DialogFooter>
    )
  }

  private focusFinding = (finding: ISelectedChangesReviewFinding) => {
    this.setState({
      focusedLocation: {
        path: finding.path,
        line: finding.line,
        side: finding.side,
      },
    })
  }

  private findDiffContext(
    snapshot: ISelectedChangesReviewSnapshot,
    finding: ISelectedChangesReviewFinding
  ): IReviewDiffContext | null {
    const file = snapshot.files.find(
      candidate => candidate.path === finding.path
    )
    if (file === undefined) {
      return null
    }

    let parsed
    try {
      parsed = new DiffParser().parse(file.diff)
    } catch {
      return null
    }

    for (const hunk of parsed.hunks) {
      const targetIndex = hunk.lines.findIndex(line =>
        this.isFindingLine(line, finding)
      )
      if (targetIndex === -1) {
        continue
      }

      const start = Math.max(0, targetIndex - ReviewContextLineCount)
      const end = Math.min(
        hunk.lines.length,
        targetIndex + ReviewContextLineCount + 1
      )
      return {
        lines: hunk.lines.slice(start, end),
        hasLinesBefore: start > 0,
        hasLinesAfter: end < hunk.lines.length,
      }
    }

    return null
  }

  private isFindingLine(line: DiffLine, finding: IReviewLocation): boolean {
    return finding.side === 'new'
      ? line.type === DiffLineType.Add && line.newLineNumber === finding.line
      : line.type === DiffLineType.Delete && line.oldLineNumber === finding.line
  }

  private getDiffLineMarker(line: DiffLine): string {
    switch (line.type) {
      case DiffLineType.Add:
        return '+'
      case DiffLineType.Delete:
        return '-'
      case DiffLineType.Context:
        return ' '
      case DiffLineType.Hunk:
        return ''
    }
  }

  private getDiffLineAriaLabel(line: DiffLine): string {
    const marker = this.getDiffLineMarker(line)
    const oldNumber =
      line.oldLineNumber === null ? '' : `old ${line.oldLineNumber}, `
    const newNumber =
      line.newLineNumber === null ? '' : `new ${line.newLineNumber}, `
    const content = line.type === DiffLineType.Hunk ? line.text : line.content
    return `${oldNumber}${newNumber}${marker}${content}`
  }

  private setFocusedLineElement = (element: HTMLDivElement | null) => {
    this.focusedLineElement = element
  }
}
