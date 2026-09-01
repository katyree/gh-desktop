import type { CodexModelsState, ICodexModel } from './codex-ipc'

export interface ICodexModelSelection {
  readonly modelId: string | null
  readonly reasoningEffort: string | null
}

/** The effective selection captured at the start of one AI operation. */
export interface ICodexModelSelectionSnapshot {
  readonly model?: string
  readonly reasoningEffort?: string
  readonly modelName: string
}

export const defaultCodexModelSelection: ICodexModelSelection = {
  modelId: null,
  reasoningEffort: null,
}

const codexModelSelectionStorageKey = 'codex-model-selection'
const MaxSelectionValueLength = 200

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function parseSelectionValue(value: unknown): string | null {
  return typeof value === 'string' &&
    value.length > 0 &&
    value.length <= MaxSelectionValueLength
    ? value
    : null
}

/** Read the persisted global selection, failing closed for malformed values. */
export function getPersistedCodexModelSelection(): ICodexModelSelection {
  const raw = localStorage.getItem(codexModelSelectionStorageKey)
  if (raw === null) {
    return defaultCodexModelSelection
  }

  try {
    const parsed: unknown = JSON.parse(raw)
    if (!isRecord(parsed)) {
      return defaultCodexModelSelection
    }
    return {
      modelId: parseSelectionValue(parsed.modelId),
      reasoningEffort: parseSelectionValue(parsed.reasoningEffort),
    }
  } catch {
    return defaultCodexModelSelection
  }
}

export function setPersistedCodexModelSelection(
  selection: ICodexModelSelection
): void {
  localStorage.setItem(codexModelSelectionStorageKey, JSON.stringify(selection))
}

export function getCodexModelForSelection(
  state: CodexModelsState | undefined,
  selection: ICodexModelSelection
): ICodexModel | undefined {
  if (state?.kind !== 'ready') {
    return undefined
  }

  if (selection.modelId === null) {
    return state.models.find(model => model.isDefault)
  }

  return state.models.find(model => model.id === selection.modelId)
}

/** Resolve and freeze the model/effort pair used by one operation. */
export function resolveCodexModelSelection(
  state: CodexModelsState | undefined,
  selection: ICodexModelSelection
): ICodexModelSelectionSnapshot {
  const model = getCodexModelForSelection(state, selection)
  if (model === undefined) {
    return { modelName: 'ChatGPT' }
  }

  const reasoningEffort =
    selection.reasoningEffort !== null &&
    model.supportedReasoningEfforts.some(
      option => option.reasoningEffort === selection.reasoningEffort
    )
      ? selection.reasoningEffort
      : undefined

  return {
    ...(selection.modelId === null ? {} : { model: model.model }),
    ...(reasoningEffort === undefined ? {} : { reasoningEffort }),
    modelName: model.displayName,
  }
}

/** Normalize renderer input while retaining unknown values until catalog load. */
export function normalizeCodexModelSelection(
  selection: ICodexModelSelection,
  state?: CodexModelsState
): ICodexModelSelection {
  if (!isRecord(selection)) {
    return defaultCodexModelSelection
  }

  const modelId = parseSelectionValue(selection.modelId)
  const reasoningEffort = parseSelectionValue(selection.reasoningEffort)
  if (modelId === null) {
    if (state?.kind === 'ready') {
      const defaultModel = state.models.find(candidate => candidate.isDefault)
      if (
        defaultModel === undefined ||
        (reasoningEffort !== null &&
          !defaultModel.supportedReasoningEfforts.some(
            option => option.reasoningEffort === reasoningEffort
          ))
      ) {
        return defaultCodexModelSelection
      }
    }
    return { modelId: null, reasoningEffort }
  }

  if (state?.kind === 'ready') {
    const selectedModel = state.models.find(
      candidate => candidate.id === modelId
    )
    if (selectedModel === undefined) {
      return defaultCodexModelSelection
    }
    if (
      reasoningEffort !== null &&
      !selectedModel.supportedReasoningEfforts.some(
        option => option.reasoningEffort === reasoningEffort
      )
    ) {
      return { modelId, reasoningEffort: null }
    }
  }

  return { modelId, reasoningEffort }
}
