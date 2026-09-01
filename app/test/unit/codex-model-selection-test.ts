import assert from 'node:assert'
import { describe, it } from 'node:test'

import type { CodexModelsState, ICodexModel } from '../../src/lib/codex-ipc'
import {
  getPersistedCodexModelSelection,
  normalizeCodexModelSelection,
  resolveCodexModelSelection,
  setPersistedCodexModelSelection,
} from '../../src/lib/codex-model-selection'

const defaultModel: ICodexModel = {
  id: 'default-id',
  model: 'default-model',
  displayName: 'Default Model',
  description: 'The recommended model.',
  isDefault: true,
  defaultReasoningEffort: 'medium',
  supportedReasoningEfforts: [
    { reasoningEffort: 'low', description: 'Faster responses.' },
    { reasoningEffort: 'medium', description: 'Balanced responses.' },
  ],
}

const alternateModel: ICodexModel = {
  id: 'alternate-id',
  model: 'alternate-model',
  displayName: 'Alternate Model',
  description: 'An alternate model.',
  isDefault: false,
  defaultReasoningEffort: 'high',
  supportedReasoningEfforts: [
    { reasoningEffort: 'high', description: 'Deeper responses.' },
  ],
}

const models: CodexModelsState = {
  kind: 'ready',
  models: [defaultModel, alternateModel],
}

describe('Codex model selection', () => {
  it('round-trips the persisted model id and reasoning effort', () => {
    const storageKey = 'codex-model-selection'
    const previous = localStorage.getItem(storageKey)
    const selection = {
      modelId: 'alternate-id',
      reasoningEffort: 'high',
    }

    try {
      setPersistedCodexModelSelection(selection)
      assert.deepStrictEqual(getPersistedCodexModelSelection(), selection)
    } finally {
      if (previous === null) {
        localStorage.removeItem(storageKey)
      } else {
        localStorage.setItem(storageKey, previous)
      }
    }
  })

  it('uses the catalog default for Automatic and keeps an explicit effort', () => {
    assert.deepStrictEqual(
      resolveCodexModelSelection(models, {
        modelId: null,
        reasoningEffort: 'low',
      }),
      {
        reasoningEffort: 'low',
        modelName: 'Default Model',
      }
    )
  })

  it('uses the protocol model while persisting the stable catalog id', () => {
    assert.deepStrictEqual(
      resolveCodexModelSelection(models, {
        modelId: 'alternate-id',
        reasoningEffort: 'high',
      }),
      {
        model: 'alternate-model',
        reasoningEffort: 'high',
        modelName: 'Alternate Model',
      }
    )
  })

  it('clears stale models and unsupported efforts once the catalog is ready', () => {
    assert.deepStrictEqual(
      normalizeCodexModelSelection(
        { modelId: 'missing-id', reasoningEffort: 'high' },
        models
      ),
      { modelId: null, reasoningEffort: null }
    )
    assert.deepStrictEqual(
      normalizeCodexModelSelection(
        { modelId: 'alternate-id', reasoningEffort: 'low' },
        models
      ),
      { modelId: 'alternate-id', reasoningEffort: null }
    )
  })
})
