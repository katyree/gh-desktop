/** Provider label displayed while ChatGPT prepares conflict suggestions. */
export interface IConflictResolutionModelDisplay {
  readonly modelName: string
}

/** Codex owns model selection for ChatGPT subscription requests. */
export function getConflictResolutionModelDisplay(): IConflictResolutionModelDisplay {
  return { modelName: 'ChatGPT' }
}
