import assert from 'node:assert'
import { describe, it } from 'node:test'

import { sanitizeCodexRateLimits } from '../../src/main-process/codex-rate-limits'

function response(primaryUsedPercent: number, secondaryUsedPercent?: number) {
  return {
    rateLimits: {
      primary: {
        usedPercent: primaryUsedPercent,
        windowDurationMins: 300,
        resetsAt: 1_788_220_800,
      },
      secondary:
        secondaryUsedPercent === undefined
          ? null
          : {
              usedPercent: secondaryUsedPercent,
              windowDurationMins: 10_080,
              resetsAt: 1_788_307_200,
            },
      spendControlReached: false,
      rateLimitReachedType: null,
    },
  }
}

describe('sanitizeCodexRateLimits', () => {
  it('maps available and near-limit windows without inventing billing data', () => {
    const available = sanitizeCodexRateLimits(response(20, 40))
    const nearLimit = sanitizeCodexRateLimits(response(85, 30))

    assert.equal(available.status, 'available')
    assert.equal(nearLimit.status, 'near-limit')
    assert.equal(nearLimit.primary?.usedPercent, 85)
    assert.equal(nearLimit.resetsAt, '2026-09-01T00:00:00.000Z')
    assert.deepStrictEqual(Object.keys(nearLimit).sort(), [
      'primary',
      'resetsAt',
      'secondary',
      'status',
    ])
  })

  it('maps exhausted usage from a full window or explicit backend state', () => {
    assert.equal(sanitizeCodexRateLimits(response(100)).status, 'exhausted')

    const base = response(20)
    const explicitlyExhausted = {
      ...base,
      rateLimits: {
        ...base.rateLimits,
        rateLimitReachedType: 'rate_limit_reached',
      },
    }
    assert.equal(
      sanitizeCodexRateLimits(explicitlyExhausted).status,
      'exhausted'
    )
  })

  it('degrades missing, changed, and invalid fields to unavailable', () => {
    const payloads = [
      null,
      {},
      { rateLimits: {} },
      {
        rateLimits: {
          primary: { usedPercentage: 50, resetsAt: 1_788_220_800 },
          secondary: null,
        },
      },
      {
        rateLimits: {
          primary: { usedPercent: Number.NaN, resetsAt: 1_788_220_800 },
          secondary: null,
        },
      },
    ]

    for (const payload of payloads) {
      assert.deepStrictEqual(sanitizeCodexRateLimits(payload), {
        status: 'unavailable',
        primary: null,
        secondary: null,
        resetsAt: null,
      })
    }
  })
})
