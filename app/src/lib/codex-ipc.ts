export type CodexJSONPrimitive = string | number | boolean | null
export type CodexJSONValue =
  | CodexJSONPrimitive
  | ReadonlyArray<CodexJSONValue>
  | { readonly [key: string]: CodexJSONValue }

/** Account data safe to retain in renderer memory. Credentials are omitted. */
export type CodexAccountStatus =
  | 'loading'
  | 'signed-out'
  | 'signing-in'
  | 'signed-in'
  | 'expired'
  | 'rate-limited'
  | 'error'

export interface ICodexAccountState {
  readonly status: CodexAccountStatus
  readonly type: 'signed-out' | 'chatgpt' | 'api-key' | 'other'
  readonly email: string | null
  readonly planType: string | null
  readonly requiresOpenaiAuth: boolean
  readonly errorMessage?: string
}

export type CodexLoginMethod = 'browser' | 'device-code'

export interface ICodexLoginStart {
  readonly method: CodexLoginMethod
  readonly loginId: string
  /** Present only for device-code login. Never persisted or logged. */
  readonly userCode?: string
}

export type CodexRateLimitStatus =
  | 'available'
  | 'near-limit'
  | 'exhausted'
  | 'unavailable'

export interface ICodexRateLimitWindow {
  readonly usedPercent: number
  readonly resetsAt: string | null
}

/** Subscription usage state safe to display in the renderer. */
export interface ICodexRateLimitState {
  readonly status: CodexRateLimitStatus
  readonly primary: ICodexRateLimitWindow | null
  readonly secondary: ICodexRateLimitWindow | null
  readonly resetsAt: string | null
}

/** One reasoning effort advertised by the active Codex model catalog. */
export interface ICodexReasoningEffort {
  readonly reasoningEffort: string
  readonly description: string
}

/** A sanitized model entry safe to expose to the renderer. */
export interface ICodexModel {
  readonly id: string
  readonly model: string
  readonly displayName: string
  readonly description: string
  readonly isDefault: boolean
  readonly defaultReasoningEffort: string
  readonly supportedReasoningEfforts: ReadonlyArray<ICodexReasoningEffort>
}

/** Discriminated state for the dynamic model catalog. */
export type CodexModelsState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly models: ReadonlyArray<ICodexModel> }
  | { readonly kind: 'unavailable' }

/** A provider-neutral generation request crossing the renderer IPC boundary. */
export interface ICodexGenerationRequest {
  readonly instructions: string
  readonly prompt: string
  readonly model?: string
  /** Optional turn-level reasoning effort override. */
  readonly reasoningEffort?: string
  readonly outputSchema?: CodexJSONValue
}

/** Opaque generation identifiers. No process or authentication state crosses IPC. */
export interface ICodexGenerationHandle {
  readonly threadId: string
  readonly turnId: string
}

export interface ICodexGenerationCancellation {
  readonly threadId: string
  readonly turnId: string
}

export type CodexGenerationOutcome =
  | 'success'
  | 'cancelled'
  | 'auth-required'
  | 'rate-limited'
  | 'timeout'
  | 'runtime-error'

/** Sanitized terminal result for one generation turn. */
export type ICodexGenerationResult =
  | {
      readonly outcome: 'success'
      readonly output: string
    }
  | {
      readonly outcome: Exclude<CodexGenerationOutcome, 'success'>
    }

/** Minimal generation bridge shared by renderer-side Codex features. */
export interface ICodexGenerationClient {
  readonly start: (
    request: ICodexGenerationRequest
  ) => Promise<ICodexGenerationHandle>
  readonly wait: (
    handle: ICodexGenerationHandle
  ) => Promise<ICodexGenerationResult>
  readonly cancel: (cancellation: ICodexGenerationCancellation) => Promise<void>
}
