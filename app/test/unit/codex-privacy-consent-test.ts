import assert from 'node:assert'
import { describe, it } from 'node:test'

import {
  CodexPrivacyConsentStorageKey,
  CodexPrivacyConsentStore,
} from '../../src/lib/codex-privacy-consent'

function createStorage(initial: string | null = null) {
  const values = new Map<string, string>()
  if (initial !== null) {
    values.set(CodexPrivacyConsentStorageKey, initial)
  }
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => values.set(key, value),
    removeItem: (key: string) => values.delete(key),
    values,
  }
}

describe('CodexPrivacyConsentStore', () => {
  it('records consent per repository without storing repository paths', () => {
    const storage = createStorage()
    const consent = new CodexPrivacyConsentStore(storage, () => 1_000)

    consent.acknowledge(42)

    assert.equal(consent.hasFreshConsent(42), true)
    assert.equal(consent.hasFreshConsent(43), false)
    const persisted = storage.values.get(CodexPrivacyConsentStorageKey) ?? ''
    assert.equal(persisted, '{"42":1000}')
    assert.equal(persisted.includes('C:\\private-repository'), false)
  })

  it('expires acknowledgements after 30 days and resets all repositories', () => {
    let now = 10_000
    const storage = createStorage()
    const consent = new CodexPrivacyConsentStore(storage, () => now)
    consent.acknowledge(1)
    consent.acknowledge(2)

    now += 31 * 24 * 60 * 60 * 1000
    assert.equal(consent.hasFreshConsent(1), false)

    consent.resetAll()
    assert.equal(consent.hasFreshConsent(2), false)
    assert.equal(storage.values.has(CodexPrivacyConsentStorageKey), false)
  })

  it('treats malformed persisted state as no consent', () => {
    const storage = createStorage('{"42":"not-a-timestamp"}')
    const consent = new CodexPrivacyConsentStore(storage, () => 1_000)

    assert.equal(consent.hasFreshConsent(42), false)
  })
})
