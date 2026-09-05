const fs = require('fs')
const readline = require('readline')

const controlPath = process.env.DESKTOP_E2E_CODEX_CONTROL_FILE
const capturePath = process.env.DESKTOP_E2E_CODEX_CAPTURE_FILE
let generation = 0
const activeTurns = new Map()

function mode() {
  try {
    return controlPath ? fs.readFileSync(controlPath, 'utf8').trim() : 'success'
  } catch {
    return 'success'
  }
}

function send(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`)
}

function capture(message) {
  if (capturePath) {
    fs.appendFileSync(capturePath, `${JSON.stringify(message)}\n`)
  }
}

function complete(threadId, turnId, output, status = 'completed') {
  send({
    method: 'turn/completed',
    params: {
      threadId,
      turn: {
        id: turnId,
        status,
        error: null,
        items:
          status === 'completed'
            ? [
                {
                  type: 'agentMessage',
                  id: `message-${turnId}`,
                  text: output,
                  phase: 'final_answer',
                  memoryCitation: null,
                  delivery: null,
                },
              ]
            : [],
        itemsView: 'full',
        startedAt: null,
        completedAt: null,
        durationMs: null,
      },
    },
  })
}

readline.createInterface({ input: process.stdin }).on('line', line => {
  const message = JSON.parse(line)
  if (message.method === 'initialized') {
    return
  }
  if (message.id === undefined) {
    return
  }

  switch (message.method) {
    case 'initialize':
      send({ id: message.id, result: { userAgent: 'fake-codex-e2e' } })
      break
    case 'account/read':
      send({
        id: message.id,
        result: {
          account: {
            type: 'chatgpt',
            email: 'test.user@example.invalid',
            planType: 'test',
          },
          requiresOpenaiAuth: true,
        },
      })
      break
    case 'account/rateLimits/read':
      send({
        id: message.id,
        result: {
          rateLimits: {
            primary:
              mode() === 'exhausted'
                ? { usedPercent: 100, resetsAt: 1_788_220_800 }
                : { usedPercent: 10, resetsAt: 1_788_220_800 },
            secondary: null,
            spendControlReached: false,
            rateLimitReachedType: null,
          },
        },
      })
      break
    case 'model/list':
      send({
        id: message.id,
        result: {
          data: [
            {
              id: 'fixture-balanced',
              model: 'gpt-5.6-terra',
              displayName: 'Balanced fixture model',
              description: 'A balanced model for deterministic E2E coverage.',
              hidden: false,
              isDefault: true,
              defaultReasoningEffort: 'medium',
              supportedReasoningEfforts: [
                { reasoningEffort: 'low', description: 'Faster responses.' },
                {
                  reasoningEffort: 'medium',
                  description: 'Balanced responses.',
                },
              ],
            },
            {
              id: 'fixture-deep',
              model: 'gpt-5.6-luna',
              displayName: 'Deep fixture model',
              description: 'A deeper model with stronger reasoning options.',
              hidden: false,
              isDefault: false,
              defaultReasoningEffort: 'high',
              supportedReasoningEfforts: [
                { reasoningEffort: 'high', description: 'Deeper responses.' },
                {
                  reasoningEffort: 'xhigh',
                  description: 'Most deliberate responses.',
                },
              ],
            },
            {
              id: 'fixture-hidden',
              model: 'gpt-hidden-fixture',
              displayName: 'Hidden fixture model',
              description: 'This model must not appear in the picker.',
              hidden: true,
              isDefault: false,
              defaultReasoningEffort: 'low',
              supportedReasoningEfforts: [
                { reasoningEffort: 'low', description: 'Faster responses.' },
              ],
            },
          ],
          nextCursor: null,
        },
      })
      break
    case 'thread/start': {
      generation++
      const threadId = `thread-${generation}`
      capture({ method: message.method, params: message.params })
      send({ id: message.id, result: { thread: { id: threadId } } })
      break
    }
    case 'turn/start': {
      const threadId = message.params.threadId
      const turnId = `turn-${generation}`
      activeTurns.set(`${threadId}:${turnId}`, { threadId, turnId })
      capture({ method: message.method, params: message.params })
      send({ id: message.id, result: { turn: { id: turnId } } })
      if (mode() !== 'slow') {
        const outputSchema = message.params.outputSchema
        const isSelectedChangesReview =
          outputSchema &&
          typeof outputSchema === 'object' &&
          outputSchema.properties &&
          typeof outputSchema.properties === 'object' &&
          outputSchema.properties.findings !== undefined
        const output =
          mode() === 'invalid'
            ? 'not json'
            : isSelectedChangesReview
            ? JSON.stringify({
                findings: [
                  {
                    path: 'smoke-change.txt',
                    line: 1,
                    side: 'new',
                    title: 'Check the selected value',
                    explanation:
                      'The selected change needs a corresponding validation.',
                    suggestion: 'Add validation before accepting the value.',
                  },
                ],
              })
            : '{"title":"Describe selected change","description":"Generated by the deterministic Codex fixture."}'
        setTimeout(() => complete(threadId, turnId, output), 10)
      }
      break
    }
    case 'turn/interrupt': {
      const key = `${message.params.threadId}:${message.params.turnId}`
      const turn = activeTurns.get(key)
      capture({ method: message.method, params: message.params })
      send({ id: message.id, result: {} })
      if (turn) {
        setTimeout(
          () => complete(turn.threadId, turn.turnId, '', 'interrupted'),
          10
        )
      }
      break
    }
    default:
      send({
        id: message.id,
        error: { code: -32601, message: `Unsupported ${message.method}` },
      })
  }
})
