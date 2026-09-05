import type { WorkingDirectoryFileChange } from '../models/status'
import type { ISelectedChangesReviewFinding } from './codex-selected-changes-review-generator'
import type { ISelectedChangesReviewSnapshot } from './selected-changes-review-snapshot'

/** The in-memory state for one selected-changes review request. */
export type SelectedChangesReviewState =
  | { readonly kind: 'idle' }
  | {
      readonly kind: 'pending'
      readonly requestId: number
      readonly selectionKey: string
      readonly snapshot: ISelectedChangesReviewSnapshot | null
    }
  | {
      readonly kind: 'complete'
      readonly requestId: number
      readonly selectionKey: string
      readonly snapshot: ISelectedChangesReviewSnapshot
      readonly findings: ReadonlyArray<ISelectedChangesReviewFinding>
    }
  | {
      readonly kind: 'outdated'
      readonly requestId: number
      readonly selectionKey: string
      readonly snapshot: ISelectedChangesReviewSnapshot | null
      readonly findings: ReadonlyArray<ISelectedChangesReviewFinding>
      readonly reason: 'selection-changed' | 'content-changed'
    }
  | {
      readonly kind: 'error'
      readonly requestId: number
      readonly selectionKey: string
      readonly snapshot: ISelectedChangesReviewSnapshot | null
      readonly message: string
    }

export const initialSelectedChangesReviewState: SelectedChangesReviewState = {
  kind: 'idle',
}

/** Identify the selected files and their whole-file or partial selection mode. */
export function getSelectedChangesReviewSelectionKey(
  filesSelected: ReadonlyArray<WorkingDirectoryFileChange>
): string {
  return filesSelected
    .map(file => `${file.id}:${file.selection.getSelectionType()}`)
    .sort()
    .join('\n')
}

/** Compare the immutable snapshot that was sent to Codex with a fresh one. */
export function selectedChangesReviewSnapshotsEqual(
  left: ISelectedChangesReviewSnapshot,
  right: ISelectedChangesReviewSnapshot
): boolean {
  if (left.diff !== right.diff || left.files.length !== right.files.length) {
    return false
  }

  const rightFiles = new Map(right.files.map(file => [file.path, file.diff]))
  return left.files.every(file => rightFiles.get(file.path) === file.diff)
}
