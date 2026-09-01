import assert from 'node:assert'
import { afterEach, beforeEach, describe, it } from 'node:test'
import * as React from 'react'

import { CodexCommitMessageDisclaimer } from '../../../src/ui/generate-commit-message/codex-commit-message-disclaimer'
import { fireEvent, render, screen } from '../../helpers/ui/render'

let restoreIpcSend: (() => void) | undefined

beforeEach(async () => {
  const electron = await import('electron')
  const previousSend = electron.ipcRenderer.send
  electron.ipcRenderer.send = () => undefined
  restoreIpcSend = () => {
    electron.ipcRenderer.send = previousSend
  }
})

afterEach(() => restoreIpcSend?.())

describe('CodexCommitMessageDisclaimer', () => {
  it('describes the exact transfer and starts only after acceptance', () => {
    let accepted = 0
    let dismissed = 0
    render(
      <CodexCommitMessageDisclaimer
        onAccepted={() => accepted++}
        onDismissed={() => dismissed++}
      />
    )

    assert.ok(screen.getByText(/diff for the files selected for this commit/))
    assert.ok(screen.getByText(/will not send unselected files/))
    assert.ok(screen.getByText(/will not create a commit automatically/))

    fireEvent.click(screen.getByText('Share selected changes'))
    assert.equal(accepted, 1)
    assert.equal(dismissed, 1)
  })

  it('dismisses without accepting or starting generation', async () => {
    let accepted = 0
    render(
      <CodexCommitMessageDisclaimer
        onAccepted={() => accepted++}
        onDismissed={() => undefined}
      />
    )

    await new Promise(resolve => setTimeout(resolve, 260))
    fireEvent.click(screen.getByText('Cancel'))
    assert.equal(accepted, 0)
  })
})
