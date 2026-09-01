import assert from 'node:assert'
import { describe, it } from 'node:test'
import { parseGeneratedCommitMessage } from '../../src/lib/commit-message-generator'

describe('parseGeneratedCommitMessage', () => {
  it('parses a provider-neutral generated message', () => {
    assert.deepEqual(
      parseGeneratedCommitMessage(
        '{"title":"Add generator seam","description":"Use a provider-independent contract."}'
      ),
      {
        title: 'Add generator seam',
        description: 'Use a provider-independent contract.',
      }
    )
  })

  it('preserves the inherited Copilot error label by default', () => {
    assert.throws(
      () => parseGeneratedCommitMessage('not json'),
      /Copilot returned invalid JSON/
    )
  })

  it('allows a provider adapter to supply its own error label', () => {
    assert.throws(
      () => parseGeneratedCommitMessage('not json', 'Test Provider'),
      /Test Provider returned invalid JSON/
    )
  })
})
