import assert from 'node:assert'
import { describe, it } from 'node:test'

import {
  buildCodexSelectedChangesReviewGenerationRequest,
  CodexSelectedChangesReviewCancelledError,
  CodexSelectedChangesReviewGenerationError,
  CodexSelectedChangesReviewGenerator,
} from '../../src/lib/codex-selected-changes-review-generator'
import type {
  ICodexGenerationClient,
  ICodexGenerationHandle,
  ICodexGenerationRequest,
  ICodexGenerationResult,
} from '../../src/lib/codex-ipc'
import type { ISelectedChangesReviewSnapshot } from '../../src/lib/selected-changes-review-snapshot'

const selectedDiff = `diff --git a/src/space file.ts b/src/space file.ts
index 1111111..2222222 100644
--- a/src/space file.ts
+++ b/src/space file.ts
@@ -1,3 +1,3 @@
 context
-const value = 1
+const value = 2
 context
`

const snapshot: ISelectedChangesReviewSnapshot = {
  diff: selectedDiff,
  files: [{ path: 'src/space file.ts', diff: selectedDiff }],
}

const validOutput = JSON.stringify({
  findings: [
    {
      path: 'src/space file.ts',
      line: 2,
      side: 'new',
      title: 'Use the selected value',
      explanation: 'The changed value keeps the dependent branch unreachable.',
      suggestion: 'Update the dependent branch or preserve the previous value.',
    },
  ],
})

function createClient(result: ICodexGenerationResult): {
  readonly client: ICodexGenerationClient
  readonly requests: ReadonlyArray<ICodexGenerationRequest>
  readonly cancellations: ReadonlyArray<ICodexGenerationHandle>
} {
  const requests = new Array<ICodexGenerationRequest>()
  const cancellations = new Array<ICodexGenerationHandle>()
  const client: ICodexGenerationClient = {
    start: async request => {
      requests.push(request)
      return { threadId: 'thread-test', turnId: 'turn-test' }
    },
    wait: async () => result,
    cancel: async handle => {
      cancellations.push(handle)
    },
  }
  return { client, requests, cancellations }
}

describe('buildCodexSelectedChangesReviewGenerationRequest', () => {
  it('keeps the request bounded to the selected diff and model snapshot', () => {
    const request = buildCodexSelectedChangesReviewGenerationRequest(snapshot, {
      model: 'gpt-selected',
      reasoningEffort: 'high',
      modelName: 'Selected model',
    })

    assert.ok(request.prompt.includes(selectedDiff))
    assert(!request.prompt.includes('unselected.ts'))
    assert.match(request.instructions, /Do not use tools/)
    assert.equal(request.model, 'gpt-selected')
    assert.equal(request.reasoningEffort, 'high')
    assert.ok(request.outputSchema)
  })
})

describe('parseCodexSelectedChangesReviewOutput through CodexSelectedChangesReviewGenerator', () => {
  it('rejects invented paths and context-line locations', async () => {
    for (const finding of [
      {
        path: '../outside.ts',
        line: 2,
        side: 'new',
        title: 'Outside',
        explanation: 'This path is not in the selected snapshot.',
        suggestion: 'Ignore it.',
      },
      {
        path: 'src/space file.ts',
        line: 1,
        side: 'new',
        title: 'Context',
        explanation: 'This line was not changed.',
        suggestion: 'Ignore it.',
      },
    ]) {
      const client = createClient({
        outcome: 'success',
        output: JSON.stringify({ findings: [finding] }),
      })

      await assert.rejects(
        new CodexSelectedChangesReviewGenerator(client.client).review({
          snapshot,
        }),
        (error: unknown) =>
          error instanceof CodexSelectedChangesReviewGenerationError &&
          error.kind === 'invalid-output'
      )
    }
  })
})

describe('CodexSelectedChangesReviewGenerator', () => {
  it('returns validated findings and maps terminal errors', async () => {
    const validClient = createClient({
      outcome: 'success',
      output: validOutput,
    })
    const findings = await new CodexSelectedChangesReviewGenerator(
      validClient.client
    ).review({ snapshot })

    assert.equal(findings.length, 1)
    assert.equal(findings[0].path, 'src/space file.ts')
    assert.equal(findings[0].line, 2)

    for (const outcome of [
      'auth-required',
      'rate-limited',
      'timeout',
      'runtime-error',
    ] as const) {
      await assert.rejects(
        new CodexSelectedChangesReviewGenerator(
          createClient({ outcome }).client
        ).review({ snapshot }),
        (error: unknown) =>
          error instanceof CodexSelectedChangesReviewGenerationError &&
          error.kind === outcome
      )
    }
  })

  it('cancels the active turn', async () => {
    const cancellations = new Array<ICodexGenerationHandle>()
    const client: ICodexGenerationClient = {
      start: async () => ({ threadId: 'thread-test', turnId: 'turn-test' }),
      wait: () => new Promise<ICodexGenerationResult>(() => undefined),
      cancel: async handle => {
        cancellations.push(handle)
      },
    }
    const controller = new AbortController()
    const review = new CodexSelectedChangesReviewGenerator(client).review({
      snapshot,
      signal: controller.signal,
    })

    await new Promise(resolve => setImmediate(resolve))
    controller.abort()

    await assert.rejects(
      review,
      (error: unknown) =>
        error instanceof CodexSelectedChangesReviewCancelledError
    )
    assert.deepStrictEqual(cancellations, [
      { threadId: 'thread-test', turnId: 'turn-test' },
    ])
  })
})
