import assert from 'node:assert'
import { EventEmitter } from 'node:events'
import { describe, it } from 'node:test'
import { WebContents, WebFrameMain } from 'electron'
import {
  addTrustedIPCSender,
  isTrustedIPCFrameSender,
  isTrustedIPCSender,
} from '../../src/main-process/trusted-ipc-sender'

describe('trusted IPC sender', () => {
  it('trusts only the registered web contents main frame', () => {
    const mainFrame = {} as WebFrameMain
    const sender = Object.assign(new EventEmitter(), {
      id: 987_654,
      mainFrame,
    }) as unknown as WebContents

    addTrustedIPCSender(sender)
    assert.equal(isTrustedIPCSender(sender), true)
    assert.equal(isTrustedIPCFrameSender(sender, mainFrame), true)
    assert.equal(isTrustedIPCFrameSender(sender, {} as WebFrameMain), false)
    assert.equal(isTrustedIPCFrameSender(sender, null), false)

    sender.emit('destroyed')
    assert.equal(isTrustedIPCSender(sender), false)
  })
})
