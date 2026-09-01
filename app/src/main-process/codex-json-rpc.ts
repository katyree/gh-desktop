import { Readable, Writable } from 'stream'
import { CodexJSONValue } from '../lib/codex-ipc'

export type CodexRequestId = string | number

export interface ICodexJSONRPCError {
  readonly code: number
  readonly message: string
  readonly data?: CodexJSONValue
}

export interface ICodexJSONRPCRequestOptions {
  readonly timeoutMs?: number
  readonly signal?: AbortSignal
}

export interface ICodexServerRequest {
  readonly id: CodexRequestId
  readonly method: string
  readonly params: CodexJSONValue | undefined
}

export interface ICodexServerNotification {
  readonly method: string
  readonly params: CodexJSONValue | undefined
}

export interface ICodexJSONRPCTransportOptions {
  readonly defaultTimeoutMs?: number
  readonly maximumLineBytes?: number
  readonly onProtocolError?: (error: Error) => void
}

interface IPendingRequest {
  readonly method: string
  readonly resolve: (result: CodexJSONValue | undefined) => void
  readonly reject: (error: Error) => void
  readonly timeout: NodeJS.Timeout
  readonly signal: AbortSignal | undefined
  readonly abortListener: (() => void) | undefined
}

type ServerRequestHandler = (
  params: CodexJSONValue | undefined
) => CodexJSONValue | undefined | Promise<CodexJSONValue | undefined>

type NotificationListener = (notification: ICodexServerNotification) => void

export class CodexJSONRPCError extends Error {
  public readonly code: number
  public readonly data: CodexJSONValue | undefined

  public constructor(method: string, error: ICodexJSONRPCError) {
    super(`${method} failed (${error.code}): ${error.message}`)
    this.name = 'CodexJSONRPCError'
    this.code = error.code
    this.data = error.data
  }
}

export class CodexJSONRPCMalformedMessageError extends Error {
  public constructor(message: string) {
    super(message)
    this.name = 'CodexJSONRPCMalformedMessageError'
  }
}

export class CodexJSONRPCTimeoutError extends Error {
  public constructor(method: string, timeoutMs: number) {
    super(`${method} timed out after ${timeoutMs}ms`)
    this.name = 'CodexJSONRPCTimeoutError'
  }
}

export class CodexJSONRPCCancelledError extends Error {
  public constructor(method: string) {
    super(`${method} was cancelled`)
    this.name = 'CodexJSONRPCCancelledError'
  }
}

export class CodexJSONRPCClosedError extends Error {
  public constructor(message = 'Codex App Server transport closed') {
    super(message)
    this.name = 'CodexJSONRPCClosedError'
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isRequestId(value: unknown): value is CodexRequestId {
  return typeof value === 'string' || typeof value === 'number'
}

function isJSONRPCError(value: unknown): value is ICodexJSONRPCError {
  return (
    isRecord(value) &&
    typeof value.code === 'number' &&
    typeof value.message === 'string'
  )
}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error))
}

/**
 * JSON-line transport for the pinned Codex App Server protocol. It owns request
 * correlation only; process lifecycle remains in CodexAppServerSupervisor.
 */
export class CodexJSONRPCTransport {
  private readonly input: Readable
  private readonly output: Writable
  private readonly defaultTimeoutMs: number
  private readonly maximumLineBytes: number
  private readonly onProtocolError: ((error: Error) => void) | undefined
  private readonly pendingRequests = new Map<number, IPendingRequest>()
  private readonly serverRequestHandlers = new Map<
    string,
    ServerRequestHandler
  >()
  private readonly notificationListeners = new Set<NotificationListener>()

  private nextRequestId = 1
  private inputBuffer = ''
  private closed = false

  public constructor(
    input: Readable,
    output: Writable,
    options: ICodexJSONRPCTransportOptions = {}
  ) {
    this.input = input
    this.output = output
    this.defaultTimeoutMs = options.defaultTimeoutMs ?? 30_000
    this.maximumLineBytes = options.maximumLineBytes ?? 4 * 1024 * 1024
    this.onProtocolError = options.onProtocolError

    if (this.defaultTimeoutMs < 1 || !Number.isFinite(this.defaultTimeoutMs)) {
      throw new Error('defaultTimeoutMs must be a positive number')
    }
    if (this.maximumLineBytes < 1 || !Number.isFinite(this.maximumLineBytes)) {
      throw new Error('maximumLineBytes must be a positive number')
    }

    input.setEncoding('utf8')
    input.on('data', this.handleData)
    input.once('end', this.handleEnd)
    input.once('error', this.handleInputError)
    output.once('error', this.handleOutputError)
  }

  public get isClosed() {
    return this.closed
  }

  public request<TResult extends CodexJSONValue | undefined = CodexJSONValue>(
    method: string,
    params?: CodexJSONValue,
    options: ICodexJSONRPCRequestOptions = {}
  ): Promise<TResult> {
    if (this.closed) {
      return Promise.reject(new CodexJSONRPCClosedError())
    }
    if (options.signal?.aborted) {
      return Promise.reject(new CodexJSONRPCCancelledError(method))
    }

    const requestId = this.nextRequestId++
    if (this.nextRequestId > Number.MAX_SAFE_INTEGER) {
      this.nextRequestId = 1
    }

    const message =
      params === undefined
        ? { id: requestId, method }
        : { id: requestId, method, params }
    let serializedMessage: string
    try {
      serializedMessage = `${JSON.stringify(message)}\n`
    } catch (error) {
      return Promise.reject(asError(error))
    }

    const timeoutMs = options.timeoutMs ?? this.defaultTimeoutMs
    if (timeoutMs < 1 || !Number.isFinite(timeoutMs)) {
      return Promise.reject(new Error('timeoutMs must be a positive number'))
    }

    const response = new Promise<TResult>((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.rejectRequest(
          requestId,
          new CodexJSONRPCTimeoutError(method, timeoutMs)
        )
      }, timeoutMs)
      timeout.unref()

      const abortListener = options.signal
        ? () =>
            this.rejectRequest(
              requestId,
              new CodexJSONRPCCancelledError(method)
            )
        : undefined
      options.signal?.addEventListener('abort', abortListener!, { once: true })

      this.pendingRequests.set(requestId, {
        method,
        resolve: result => resolve(result as TResult),
        reject,
        timeout,
        signal: options.signal,
        abortListener,
      })
    })

    try {
      this.output.write(serializedMessage, 'utf8')
    } catch (error) {
      this.rejectRequest(requestId, asError(error))
    }

    return response
  }

  public notify(method: string, params?: CodexJSONValue) {
    if (this.closed) {
      throw new CodexJSONRPCClosedError()
    }
    const message = params === undefined ? { method } : { method, params }
    this.writeMessage(message)
  }

  public handleServerRequest(
    method: string,
    handler: ServerRequestHandler
  ): () => void {
    if (this.serverRequestHandlers.has(method)) {
      throw new Error(`A handler is already registered for ${method}`)
    }
    this.serverRequestHandlers.set(method, handler)
    return () => {
      if (this.serverRequestHandlers.get(method) === handler) {
        this.serverRequestHandlers.delete(method)
      }
    }
  }

  public onNotification(listener: NotificationListener): () => void {
    this.notificationListeners.add(listener)
    return () => this.notificationListeners.delete(listener)
  }

  public close(error: Error = new CodexJSONRPCClosedError()) {
    if (this.closed) {
      return
    }
    this.closed = true
    this.input.removeListener('data', this.handleData)

    for (const requestId of [...this.pendingRequests.keys()]) {
      this.rejectRequest(requestId, error)
    }
    this.serverRequestHandlers.clear()
    this.notificationListeners.clear()
    this.inputBuffer = ''
  }

  private readonly handleData = (chunk: string | Buffer) => {
    this.inputBuffer += String(chunk)

    let newlineIndex = this.inputBuffer.indexOf('\n')
    while (newlineIndex !== -1) {
      const line = this.inputBuffer.slice(0, newlineIndex).replace(/\r$/, '')
      this.inputBuffer = this.inputBuffer.slice(newlineIndex + 1)
      if (line.length > 0) {
        this.handleLine(line)
      }
      newlineIndex = this.inputBuffer.indexOf('\n')
    }

    if (Buffer.byteLength(this.inputBuffer, 'utf8') > this.maximumLineBytes) {
      this.inputBuffer = ''
      this.handleMalformedMessage(
        new CodexJSONRPCMalformedMessageError(
          `Codex App Server output exceeded ${this.maximumLineBytes} bytes without a newline`
        )
      )
    }
  }

  private readonly handleEnd = () => {
    if (this.inputBuffer.trim().length > 0) {
      this.handleLine(this.inputBuffer)
    }
    this.close(new CodexJSONRPCClosedError('Codex App Server reached EOF'))
  }

  private readonly handleInputError = (error: Error) => {
    this.close(
      new CodexJSONRPCClosedError(
        `Codex App Server output failed: ${error.message}`
      )
    )
  }

  private readonly handleOutputError = (error: Error) => {
    this.close(
      new CodexJSONRPCClosedError(
        `Codex App Server input failed: ${error.message}`
      )
    )
  }

  private handleLine(line: string) {
    let message: unknown
    try {
      message = JSON.parse(line)
    } catch {
      this.handleMalformedMessage(
        new CodexJSONRPCMalformedMessageError(
          'Codex App Server emitted malformed JSON'
        )
      )
      return
    }

    if (!isRecord(message)) {
      this.handleMalformedMessage(
        new CodexJSONRPCMalformedMessageError(
          'Codex App Server emitted a non-object message'
        )
      )
      return
    }

    if (typeof message.method === 'string') {
      if (isRequestId(message.id)) {
        void this.dispatchServerRequest({
          id: message.id,
          method: message.method,
          params: message.params as CodexJSONValue | undefined,
        })
      } else if (message.id === undefined) {
        this.dispatchNotification({
          method: message.method,
          params: message.params as CodexJSONValue | undefined,
        })
      } else {
        this.handleMalformedMessage(
          new CodexJSONRPCMalformedMessageError(
            'Codex App Server emitted a request with an invalid id'
          )
        )
      }
      return
    }

    if (isRequestId(message.id)) {
      this.handleResponse(message.id, message)
      return
    }

    this.handleMalformedMessage(
      new CodexJSONRPCMalformedMessageError(
        'Codex App Server emitted an unrecognized message'
      )
    )
  }

  private handleResponse(id: CodexRequestId, message: Record<string, unknown>) {
    if (typeof id !== 'number') {
      this.handleMalformedMessage(
        new CodexJSONRPCMalformedMessageError(
          `Codex App Server returned an unexpected response id: ${String(id)}`
        )
      )
      return
    }

    const pending = this.pendingRequests.get(id)
    if (pending === undefined) {
      return
    }

    const hasResult = Object.prototype.hasOwnProperty.call(message, 'result')
    const hasError = Object.prototype.hasOwnProperty.call(message, 'error')
    if (hasResult === hasError) {
      this.rejectRequest(
        id,
        new CodexJSONRPCMalformedMessageError(
          `${pending.method} received an invalid response envelope`
        )
      )
      return
    }

    if (hasError) {
      if (!isJSONRPCError(message.error)) {
        this.rejectRequest(
          id,
          new CodexJSONRPCMalformedMessageError(
            `${pending.method} received an invalid error response`
          )
        )
      } else {
        this.rejectRequest(
          id,
          new CodexJSONRPCError(pending.method, message.error)
        )
      }
      return
    }

    this.resolveRequest(id, message.result as CodexJSONValue | undefined)
  }

  private async dispatchServerRequest(request: ICodexServerRequest) {
    const handler = this.serverRequestHandlers.get(request.method)
    if (handler === undefined) {
      this.writeMessage({
        id: request.id,
        error: {
          code: -32601,
          message: `Unsupported server request: ${request.method}`,
        },
      })
      return
    }

    try {
      const result = await handler(request.params)
      this.writeMessage({ id: request.id, result: result ?? null })
    } catch (error) {
      this.writeMessage({
        id: request.id,
        error: { code: -32000, message: asError(error).message },
      })
    }
  }

  private dispatchNotification(notification: ICodexServerNotification) {
    for (const listener of this.notificationListeners) {
      try {
        listener(notification)
      } catch (error) {
        this.reportProtocolError(asError(error))
      }
    }
  }

  private handleMalformedMessage(error: Error) {
    this.reportProtocolError(error)
    const oldestRequestId = this.pendingRequests.keys().next().value as
      | number
      | undefined
    if (oldestRequestId !== undefined) {
      this.rejectRequest(oldestRequestId, error)
    }
  }

  private reportProtocolError(error: Error) {
    try {
      this.onProtocolError?.(error)
    } catch {
      // Diagnostics must not corrupt protocol processing.
    }
  }

  private resolveRequest(id: number, result: CodexJSONValue | undefined) {
    const pending = this.removePendingRequest(id)
    pending?.resolve(result)
  }

  private rejectRequest(id: number, error: Error) {
    const pending = this.removePendingRequest(id)
    pending?.reject(error)
  }

  private removePendingRequest(id: number) {
    const pending = this.pendingRequests.get(id)
    if (pending === undefined) {
      return undefined
    }
    this.pendingRequests.delete(id)
    clearTimeout(pending.timeout)
    if (pending.abortListener !== undefined) {
      pending.signal?.removeEventListener('abort', pending.abortListener)
    }
    return pending
  }

  private writeMessage(message: Record<string, unknown>) {
    if (this.closed) {
      return
    }
    try {
      this.output.write(`${JSON.stringify(message)}\n`, 'utf8')
    } catch (error) {
      this.close(asError(error))
    }
  }
}
