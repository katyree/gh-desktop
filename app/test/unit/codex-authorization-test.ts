import assert from 'node:assert'
import { describe, it } from 'node:test'
import { validateCodexAuthorizationURL } from '../../src/main-process/codex-authorization'

describe('validateCodexAuthorizationURL', () => {
  it('accepts official HTTPS browser and device login pages', () => {
    assert.equal(
      validateCodexAuthorizationURL(
        'https://auth.openai.com/oauth/authorize?client_id=test'
      ),
      'https://auth.openai.com/oauth/authorize?client_id=test'
    )
    assert.equal(
      validateCodexAuthorizationURL('https://chatgpt.com/codex/device'),
      'https://chatgpt.com/codex/device'
    )
  })

  it('rejects non-HTTPS, lookalike, and credential-bearing URLs', () => {
    for (const url of [
      'http://auth.openai.com/oauth/authorize',
      'https://auth.openai.com.example.invalid/oauth/authorize',
      'https://user:password@auth.openai.com/oauth/authorize',
      'not-a-url',
    ]) {
      assert.throws(() => validateCodexAuthorizationURL(url))
    }
  })
})
