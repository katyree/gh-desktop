import type {
  IConflictContextCommit,
  IConflictContextPullRequest,
  IConflictResolutionContext,
  IFileConflictContext,
} from './copilot-conflict-context'
import {
  createDependencyAwareChunks,
  fallbackReferencedContext,
  reassembleResolutions,
  selectReferencedContext,
  validateResolutionPaths,
} from './copilot-conflict-resolution'
import { ManualConflictResolution } from '../models/manual-conflict-resolution'
import type {
  IConflictContextReference,
  IFileResolution,
  IHunkResolution,
  IRawFileResolution,
  ICopilotConflictReference,
  ICopilotResolutionSummary,
  ICopilotSkippedFile,
} from './copilot-conflict-resolution'

/** Provider-independent input gathered for one conflict-suggestion request. */
export type IConflictSuggestionInput = IConflictResolutionContext

/** A conflicted file and its bounded marker context. */
export type IConflictSuggestionFile = IFileConflictContext

/** A complete, review-only suggestion for one conflicted file. */
export type IConflictFileSuggestion = IFileResolution

/** A conflicted file that could not safely be sent to the provider. */
export type IConflictSkippedFile = ICopilotSkippedFile

/** Progress safe for application state and UI; model reasoning is excluded. */
export interface IConflictSuggestionProgress {
  readonly phase: 'generating' | 'validating'
  readonly filesResolved: number
  readonly filesTotal: number
}

/** Display-ready explanation and references for a suggestion set. */
export type IConflictSuggestionSummary = ICopilotResolutionSummary

/** A single conflict-hunk suggestion before full-file reassembly. */
export type IConflictHunkSuggestion = IHunkResolution

/** A provider response for one file before full-file reassembly. */
export type IRawConflictFileSuggestion = IRawFileResolution

/** A model-supplied reference before it is resolved against known context. */
export type IConflictSuggestionReference = ICopilotConflictReference

/** A reference resolved against context already gathered by the application. */
export type IResolvedConflictSuggestionReference = IConflictContextReference

/** Pull-request context that may be supplied to a suggestion provider. */
export type IConflictSuggestionPullRequest = IConflictContextPullRequest

/** Commit context that may be supplied to a suggestion provider. */
export type IConflictSuggestionCommit = IConflictContextCommit

/** Terminal review data returned by a conflict-suggestion provider. */
export interface IConflictSuggestionResult {
  readonly suggestions: ReadonlyArray<IConflictFileSuggestion>
  readonly summaryMarkdown: string | null
  readonly references: ReadonlyArray<IConflictSuggestionReference>
  readonly skippedFiles: ReadonlyArray<IConflictSkippedFile>
}

/** Options shared by all conflict-suggestion providers. */
export interface IConflictSuggestionOptions {
  readonly signal?: AbortSignal
  readonly onProgress?: (progress: IConflictSuggestionProgress) => void
}

/** A provider boundary that produces suggestions but never applies them. */
export interface IConflictSuggestionProvider {
  suggest(
    input: IConflictSuggestionInput,
    options?: IConflictSuggestionOptions
  ): Promise<IConflictSuggestionResult>
}

/** Split bounded file inputs while keeping dependency-related files together. */
export const createConflictSuggestionChunks = createDependencyAwareChunks

/** Reject suggestions for paths that were not part of the input. */
export const validateConflictSuggestionPaths = validateResolutionPaths

/** Reassemble validated hunk suggestions without writing to disk. */
export const reassembleConflictSuggestions = reassembleResolutions

/**
 * Return only generated suggestions which the user has left selected. A manual
 * choice for a path is an explicit rejection of that generated suggestion.
 */
export function getSuggestedResolutionsToApply(
  suggestions: ReadonlyArray<IFileResolution>,
  manualResolutions: ReadonlyMap<string, ManualConflictResolution>
): ReadonlyArray<IFileResolution> {
  return suggestions.filter(
    suggestion => !manualResolutions.has(suggestion.path)
  )
}

export { fallbackReferencedContext, selectReferencedContext }

// Transitional names keep the existing state shape stable while providers are
// migrated. New application and UI code should use the names above.
export type {
  IConflictContextReference,
  IFileResolution,
  ICopilotResolutionSummary,
  ICopilotSkippedFile,
}

export type IConflictResolutionProgress = IConflictSuggestionProgress
