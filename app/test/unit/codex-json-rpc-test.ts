import assert from 'node:assert'
import { PassThrough } from 'node:stream'
import { describe, it } from 'node:test'
import {
  CodexJSONRPCMalformedMessageError,
  CodexJSONRPCCancelledError,
  CodexJSONRPCClosedError,
  CodexJSONRPCTimeoutError,
  CodexJSONRPCTransport,
} from '../../src/main-process/codex-json-rpc'

function createTransport(defaultTimeoutMs = 1_000) {
  const input = new PassThrough()
  const output = new PassThrough()
  let outputText = ''
  output.setEncoding('utf8')
  output.on('data', chunk => {
    outputText += String(chunk)
  })
  const transport = new CodexJSONRPCTransport(input, output, {
    defaultTimeoutMs,
  })
  const messages = () =>
    outputText
      .trim()
      .split('\n')
      .filter(Boolean)
      .map(line => JSON.parse(line) as Record<string, unknown>)
  return { input, messages, output, transport }
}

describe('CodexJSONRPCTransport', () => {
  it('correlates interleaved responses with their callers', async () => {
    const { input, messages, transport } = createTransport()
    const first = transport.request('first', { value: 1 })
    const second = transport.request('second', { value: 2 })
    assert.deepStrictEqual(
      messages().map(message => message.id),
      [1, 2]
    )

    input.write('{"id":2,"result":{"name":"second"}}\n')
    input.write('{"id":1,"result":{"name":"first"}}\n')

    assert.deepStrictEqual(await first, { name: 'first' })
    assert.deepStrictEqual(await second, { name: 'second' })
    transport.close()
  })

  it('fails one request for malformed JSON and continues parsing', async () => {
    const { input, transport } = createTransport()
    const first = transport.request('first').catch(error => error)
    const second = transport.request('second')

    input.write('{not-json}\n')
    input.write('{"id":2,"result":"still-valid"}\n')

    assert((await first) instanceof CodexJSONRPCMalformedMessageError)
    assert.equal(await second, 'still-valid')

    const third = transport.request('third')
    input.write('{"id":3,"result":"later-message"}\n')
    assert.equal(await third, 'later-message')
    transport.close()
  })

  it('rejects pending requests at EOF', async () => {
    const { input, transport } = createTransport()
    const pending = transport.request('pending')
    input.end()

    await assert.rejects(pending, error => {
      assert(error instanceof CodexJSONRPCClosedError)
      assert.match(error.message, /EOF/)
      return true
    })
  })

  it('times out and cancels requests independently', async () => {
    const { transport } = createTransport(5)
    const timedOut = transport.request('slow')
    await assert.rejects(timedOut, CodexJSONRPCTimeoutError)

    const abortController = new AbortController()
    const cancelled = transport.request('cancelled', undefined, {
      signal: abortController.signal,
    })
    abortController.abort()
    await assert.rejects(cancelled, CodexJSONRPCCancelledError)
    transport.close()
  })

  it('delivers notifications without affecting pending requests', async () => {
    const { input, transport } = createTransport()
    const notifications = new Array<string>()
    transport.onNotification(notification => {
      notifications.push(notification.method)
    })
    const pending = transport.request('pending')

    input.write('{"method":"turn/started","params":{"turnId":"t1"}}\n')
    input.write('{"id":1,"result":{"ok":true}}\n')

    assert.deepStrictEqual(notifications, ['turn/started'])
    assert.deepStrictEqual(await pending, { ok: true })
    transport.close()
  })

  it('responds to allowlisted server requests and rejects unknown methods', async () => {
    const { input, messages, transport } = createTransport()
    transport.handleServerRequest('safe/read', params => ({ echoed: params! }))

    input.write('{"id":"server-1","method":"safe/read","params":{"value":1}}\n')
    input.write('{"id":"server-2","method":"unsafe/write","params":{}}\n')
    await new Promise(resolve => setImmediate(resolve))

    const responses = messages()
    assert.deepStrictEqual(responses[0], {
      id: 'server-2',
      error: {
        code: -32601,
        message: 'Unsupported server request: unsafe/write',
      },
    })
    assert.deepStrictEqual(responses[1], {
      id: 'server-1',
      result: { echoed: { value: 1 } },
    })
    transport.close()
  })

  it('rejects pending work when explicitly shut down', async () => {
    const { transport } = createTransport()
    const pending = transport.request('pending')
    transport.close()

    await assert.rejects(pending, CodexJSONRPCClosedError)
    await assert.rejects(
      transport.request('after-close'),
      CodexJSONRPCClosedError
    )
  })
})
