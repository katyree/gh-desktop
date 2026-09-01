import assert from 'node:assert'
import { describe, it } from 'node:test'

import {
  CodexConflictSuggestionCancelledError,
  CodexConflictSuggestionError,
  CodexConflictSuggestionGenerator,
} from '../../src/lib/codex-conflict-suggestion-generator'
import type {
  ICodexGenerationClient,
  ICodexGenerationHandle,
  ICodexGenerationRequest,
  ICodexGenerationResult,
} from '../../src/lib/codex-ipc'
import type { IConflictSuggestionInput } from '../../src/lib/conflict-resolution-contract'

function inputFile(path: string) {
  return {
    path,
    hunks: [
      {
        oursContent: 'const value = 1',
        theirsContent: 'const value = 2',
        baseContent: 'const value = 0',
        contextBefore: '',
        contextAfter: '',
      },
    ],
    rawContent:
      '<<<<<<< ours\nconst value = 1\n=======\nconst value = 2\n>>>>>>> theirs',
  }
}

function makeInput(fileCount = 1): IConflictSuggestionInput {
  return {
    ourLabel: 'main',
    theirLabel: 'feature',
    files: Array.from({ length: fileCount }, (_, index) =>
      inputFile(`src/file-${index}.ts`)
    ),
    pullRequests: [],
    ourCommits: [],
    theirCommits: [],
  }
}

function responseFor(
  path: string | ReadonlyArray<string>
): ICodexGenerationResult {
  const paths = typeof path === 'string' ? [path] : path
  return {
    outcome: 'success',
    output: JSON.stringify({
      summary: '### Conflicting changes\nBoth sides changed value.',
      references: [],
      resolutions: paths.map(filePath => ({
        path: filePath,
        hunks: [{ resolvedContent: 'const value = 2' }],
        reasoning: 'The incoming value is intentional.',
      })),
    }),
  }
}

class FakeGenerationClient implements ICodexGenerationClient {
  public readonly requests: Array<ICodexGenerationRequest> = []
  public readonly cancellations: Array<ICodexGenerationHandle> = []

  public constructor(
    private readonly results: ReadonlyArray<ICodexGenerationResult>
  ) {}

  public async start(request: ICodexGenerationRequest) {
    const index = this.requests.push(request) - 1
    return { threadId: `thread-${index}`, turnId: `turn-${index}` }
  }

  public async wait(handle: ICodexGenerationHandle) {
    return this.results[Number(handle.threadId.slice('thread-'.length))]
  }

  public async cancel(handle: ICodexGenerationHandle) {
    this.cancellations.push(handle)
  }
}

describe('CodexConflictSuggestionGenerator', () => {
  it('reuses one model and reasoning snapshot across conflict chunks', async () => {
    const client = new FakeGenerationClient([
      responseFor(
        Array.from({ length: 20 }, (_, index) => `src/file-${index}.ts`)
      ),
      responseFor('src/file-20.ts'),
    ])
    const generator = new CodexConflictSuggestionGenerator(client, {
      model: 'gpt-selected',
      reasoningEffort: 'high',
      modelName: 'Selected model',
    })

    const result = await generator.suggest(makeInput(21))

    assert.equal(result.suggestions.length, 21)
    assert.equal(client.requests.length, 2)
    for (const request of client.requests) {
      assert.equal(request.model, 'gpt-selected')
      assert.equal(request.reasoningEffort, 'high')
    }
  })

  it('passes the selected model and reasoning effort to each request', async () => {
    const client = new FakeGenerationClient([responseFor('src/file-0.ts')])
    const generator = new CodexConflictSuggestionGenerator(client, {
      model: 'gpt-selected',
      reasoningEffort: 'xhigh',
      modelName: 'Selected model',
    })

    await generator.suggest(makeInput())

    assert.equal(client.requests[0].model, 'gpt-selected')
    assert.equal(client.requests[0].reasoningEffort, 'xhigh')
  })

  it('passes an explicit effort while leaving Automatic model selection unset', async () => {
    const client = new FakeGenerationClient([responseFor('src/file-0.ts')])
    const generator = new CodexConflictSuggestionGenerator(client, {
      reasoningEffort: 'low',
      modelName: 'Default model',
    })

    await generator.suggest(makeInput())

    assert.equal(client.requests[0].model, undefined)
    assert.equal(client.requests[0].reasoningEffort, 'low')
  })

  it('returns validated review-only suggestions for input paths', async () => {
    const client = new FakeGenerationClient([responseFor('src/file-0.ts')])
    const generator = new CodexConflictSuggestionGenerator(client)

    const result = await generator.suggest(makeInput())

    assert.equal(result.suggestions.length, 1)
    assert.equal(result.suggestions[0].path, 'src/file-0.ts')
    assert.equal(result.suggestions[0].resolvedContent, 'const value = 2')
    assert.equal(result.skippedFiles.length, 0)
    assert.ok(client.requests[0].outputSchema)
    assert.match(client.requests[0].instructions, /Do not use tools/)
    assert.doesNotMatch(client.requests[0].prompt, /rawContent/)
  })

  it('skips paths rejected before prompting', async () => {
    const input: IConflictSuggestionInput = {
      ...makeInput(),
      files: [{ path: 'asset.bin', hunks: [], skippedReason: 'Binary file' }],
    }
    const client = new FakeGenerationClient([])

    const result = await new CodexConflictSuggestionGenerator(client).suggest(
      input
    )

    assert.equal(client.requests.length, 0)
    assert.deepEqual(result.skippedFiles, [
      { path: 'asset.bin', reason: 'Binary file' },
    ])
  })

  it('rejects a response path that was not in the input', async () => {
    const client = new FakeGenerationClient([responseFor('../outside.ts')])

    const result = await new CodexConflictSuggestionGenerator(client).suggest(
      makeInput()
    )

    assert.equal(result.suggestions.length, 0)
    assert.equal(result.skippedFiles[0].path, 'src/file-0.ts')
  })

  it('keeps valid chunks when a later chunk is malformed', async () => {
    const client = new FakeGenerationClient([
      responseFor(
        Array.from({ length: 20 }, (_, index) => `src/file-${index}.ts`)
      ),
      { outcome: 'success', output: 'not-json' },
    ])

    const result = await new CodexConflictSuggestionGenerator(client).suggest(
      makeInput(21)
    )

    assert.equal(client.requests.length, 2)
    assert.equal(result.suggestions.length, 20)
    assert.equal(result.suggestions[0].path, 'src/file-0.ts')
    assert.equal(result.skippedFiles.length, 1)
    assert.equal(result.skippedFiles[0].path, 'src/file-20.ts')
  })

  it('propagates authentication failures', async () => {
    const client = new FakeGenerationClient([{ outcome: 'auth-required' }])

    await assert.rejects(
      new CodexConflictSuggestionGenerator(client).suggest(makeInput()),
      (error: unknown) =>
        error instanceof CodexConflictSuggestionError &&
        error.kind === 'auth-required'
    )
  })

  it('keeps completed chunks when usage is exhausted later', async () => {
    const client = new FakeGenerationClient([
      responseFor(
        Array.from({ length: 20 }, (_, index) => `src/file-${index}.ts`)
      ),
      { outcome: 'rate-limited' },
    ])

    const result = await new CodexConflictSuggestionGenerator(client).suggest(
      makeInput(21)
    )

    assert.equal(result.suggestions.length, 20)
    assert.deepEqual(result.skippedFiles, [
      {
        path: 'src/file-20.ts',
        reason:
          'ChatGPT usage was exhausted before this file could be analyzed.',
      },
    ])
  })

  it('cancels the active turn and starts no remaining chunks', async () => {
    let releaseWait: ((result: ICodexGenerationResult) => void) | undefined
    const client = new FakeGenerationClient([])
    client.wait = () =>
      new Promise<ICodexGenerationResult>(resolve => {
        releaseWait = resolve
      })
    const controller = new AbortController()
    const generation = new CodexConflictSuggestionGenerator(client).suggest(
      makeInput(21),
      { signal: controller.signal }
    )

    await new Promise(resolve => setImmediate(resolve))
    controller.abort()

    await assert.rejects(
      generation,
      (error: unknown) => error instanceof CodexConflictSuggestionCancelledError
    )
    assert.equal(client.requests.length, 1)
    assert.equal(client.cancellations.length, 1)
    releaseWait?.({ outcome: 'cancelled' })
  })
})
