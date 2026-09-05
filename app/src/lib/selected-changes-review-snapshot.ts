import * as Path from 'path'
import { unlink } from 'fs/promises'

import {
  DiffLineType,
  DiffSelectionType,
  DiffType,
  ITextDiff,
} from '../models/diff'
import {
  AppFileStatusKind,
  AppFileStatus,
  WorkingDirectoryFileChange,
} from '../models/status'
import { Repository } from '../models/repository'
import { DiffParser } from './diff-parser'
import { getTempFilePath } from './file-system'
import { formatPatch } from './patch-formatter'
import { git, isMaxBufferExceededError } from './git/core'
import { NullTreeSHA } from './git/diff-index'

const MaxReviewSnapshotBytes = 10 * 1024 * 1024

export interface ISelectedChangesReviewSnapshot {
  readonly diff: string
  readonly files: ReadonlyArray<{
    readonly path: string
    readonly diff: string
  }>
}

/** A safe, user-facing failure while building a selected-changes snapshot. */
export class SelectedChangesReviewSnapshotError extends Error {
  public constructor(message: string) {
    super(message)
    this.name = 'SelectedChangesReviewSnapshotError'
  }
}

type TemporaryIndex = {
  readonly env: { readonly GIT_INDEX_FILE: string }
  readonly headTree: string
}

type SnapshotFile = {
  readonly path: string
  readonly diff: string
}

function throwDiffTooLarge(path?: string): never {
  const fileDescription = path === undefined ? '' : ` for ${path}`
  throw new SelectedChangesReviewSnapshotError(
    `Selected changes review diff${fileDescription} exceeds the 10 MiB limit`
  )
}

function assertDiffSize(size: number, path?: string): void {
  if (size > MaxReviewSnapshotBytes) {
    throwDiffTooLarge(path)
  }
}

function normalizeRepositoryPath(
  repositoryPath: string,
  inputPath: string
): string {
  const absolutePath = Path.isAbsolute(inputPath)
    ? Path.normalize(inputPath)
    : Path.resolve(repositoryPath, inputPath)
  const repositoryRoot = Path.resolve(repositoryPath)
  const relativePath = Path.relative(repositoryRoot, absolutePath)

  if (
    relativePath.length === 0 ||
    Path.isAbsolute(relativePath) ||
    relativePath === '..' ||
    relativePath.startsWith(`..${Path.sep}`)
  ) {
    throw new SelectedChangesReviewSnapshotError(
      `Path is outside the repository: ${inputPath}`
    )
  }

  return relativePath.split(Path.sep).join('/')
}

function pathspec(path: string): string {
  return `:(top,literal)${path}`
}

function normalizeFile(
  repository: Repository,
  file: WorkingDirectoryFileChange
): WorkingDirectoryFileChange {
  const path = normalizeRepositoryPath(repository.path, file.path)
  let status: AppFileStatus = file.status

  switch (file.status.kind) {
    case AppFileStatusKind.Copied:
    case AppFileStatusKind.Renamed:
      status = {
        ...file.status,
        oldPath: normalizeRepositoryPath(repository.path, file.status.oldPath),
      }
      break
  }

  return new WorkingDirectoryFileChange(path, status, file.selection)
}

function isNoIndexSource(file: WorkingDirectoryFileChange): boolean {
  return (
    file.status.kind === AppFileStatusKind.New ||
    file.status.kind === AppFileStatusKind.Copied ||
    file.status.kind === AppFileStatusKind.Untracked
  )
}

function isUnsupportedFile(file: WorkingDirectoryFileChange): boolean {
  return file.status.kind === AppFileStatusKind.Conflicted
}

function parseTextDiff(
  output: Buffer,
  filePath: string,
  context: string
): ITextDiff {
  assertDiffSize(output.length, filePath)

  const pieces = output.toString('utf8').split('\0')
  const diffText = pieces.at(-1)
  if (diffText === undefined) {
    throw new SelectedChangesReviewSnapshotError(
      `Unable to parse ${context} for ${filePath}`
    )
  }

  const rawDiff = new DiffParser().parse(diffText)
  if (rawDiff.isBinary) {
    throw new SelectedChangesReviewSnapshotError(
      `Selected changes review does not support binary files: ${filePath}`
    )
  }

  return {
    kind: DiffType.Text,
    text: rawDiff.contents,
    hunks: rawDiff.hunks,
    maxLineNumber: rawDiff.maxLineNumber,
    hasHiddenBidiChars: rawDiff.hasHiddenBidiChars,
  }
}

async function getBoundedDiffOutput(
  args: string[],
  repository: Repository,
  name: string,
  options: {
    readonly env?: { readonly GIT_INDEX_FILE: string }
    readonly stdin?: string
    readonly successExitCodes?: ReadonlySet<number>
  }
): Promise<Buffer> {
  try {
    const result = await git(args, repository.path, name, {
      env: options.env,
      stdin: options.stdin,
      successExitCodes: options.successExitCodes ?? new Set([0]),
      maxBuffer: MaxReviewSnapshotBytes + 1,
      encoding: 'buffer',
    })
    assertDiffSize(result.stdout.length)
    return result.stdout
  } catch (error) {
    if (isMaxBufferExceededError(error)) {
      throwDiffTooLarge()
    }
    throw error
  }
}

async function initializeTemporaryIndex(
  repository: Repository,
  indexPath: string,
  headTree: string
): Promise<TemporaryIndex> {
  const env = { GIT_INDEX_FILE: indexPath }
  await git(
    ['read-tree', headTree],
    repository.path,
    'initializeSelectedChangesReviewIndex',
    { env }
  )
  return { env, headTree }
}

async function updateTemporaryIndex(
  repository: Repository,
  index: TemporaryIndex,
  paths: ReadonlyArray<string>,
  forceRemove: boolean = false
): Promise<void> {
  if (paths.length === 0) {
    return
  }

  const args = ['update-index', '--add', '--remove']
  if (forceRemove) {
    args.push('--force-remove')
  }
  args.push('--replace', '-z', '--stdin')

  await git(args, repository.path, 'updateSelectedChangesReviewIndex', {
    env: index.env,
    stdin: paths.join('\0'),
  })
}

async function stageAllSelectedFiles(
  repository: Repository,
  index: TemporaryIndex,
  files: ReadonlyArray<WorkingDirectoryFileChange>
): Promise<void> {
  const allFiles = files.filter(
    file => file.selection.getSelectionType() === DiffSelectionType.All
  )
  const oldRenamedPaths = new Array<string>()
  for (const file of allFiles) {
    if (file.status.kind === AppFileStatusKind.Renamed) {
      oldRenamedPaths.push(file.status.oldPath)
    }
  }
  const paths = allFiles.map(file => file.path)
  const deletedPaths = new Array<string>()
  for (const file of allFiles) {
    if (file.status.kind === AppFileStatusKind.Deleted) {
      deletedPaths.push(file.path)
    }
  }

  await updateTemporaryIndex(repository, index, oldRenamedPaths, true)
  await updateTemporaryIndex(repository, index, paths)
  await updateTemporaryIndex(repository, index, deletedPaths, true)
}

async function preparePartialRename(
  repository: Repository,
  index: TemporaryIndex,
  file: WorkingDirectoryFileChange
): Promise<void> {
  if (file.status.kind !== AppFileStatusKind.Renamed) {
    return
  }

  await updateTemporaryIndex(repository, index, [file.status.oldPath], true)

  const oldFile = await git(
    ['ls-tree', index.headTree, '--', pathspec(file.status.oldPath)],
    repository.path,
    'prepareSelectedChangesReviewRename',
    { env: index.env }
  )
  const tabIndex = oldFile.stdout.indexOf('\t')
  const metadata = tabIndex === -1 ? '' : oldFile.stdout.slice(0, tabIndex)
  const [mode, objectKind, objectId] = metadata.split(' ')

  if (
    tabIndex === -1 ||
    mode === undefined ||
    objectKind !== 'blob' ||
    objectId === undefined
  ) {
    throw new SelectedChangesReviewSnapshotError(
      `Unable to prepare the original path for renamed file: ${file.path}`
    )
  }

  await git(
    ['update-index', '--add', '--cacheinfo', mode, objectId, file.path],
    repository.path,
    'prepareSelectedChangesReviewRename',
    { env: index.env }
  )
}

async function getWorkingDirectoryTextDiff(
  repository: Repository,
  index: TemporaryIndex,
  file: WorkingDirectoryFileChange
): Promise<ITextDiff> {
  const args = [
    'diff',
    '--no-ext-diff',
    '--no-textconv',
    '--patch-with-raw',
    '-z',
    '--no-color',
  ]

  if (isNoIndexSource(file)) {
    args.push('--no-index', '--', '/dev/null', file.path)
  } else {
    args.push('--', pathspec(file.path))
  }

  const output = await getBoundedDiffOutput(
    args,
    repository,
    'getSelectedChangesReviewWorkingDiff',
    {
      env: index.env,
      successExitCodes: new Set([0, 1]),
    }
  )
  return parseTextDiff(output, file.path, 'working directory diff')
}

function hasSelectedChange(
  file: WorkingDirectoryFileChange,
  diff: ITextDiff
): boolean {
  return diff.hunks.some(hunk =>
    hunk.lines.some((line, lineIndex) => {
      if (line.type !== DiffLineType.Add && line.type !== DiffLineType.Delete) {
        return false
      }

      return file.selection.isSelected(hunk.unifiedDiffStart + lineIndex)
    })
  )
}

async function applyPartialSelection(
  repository: Repository,
  index: TemporaryIndex,
  file: WorkingDirectoryFileChange
): Promise<boolean> {
  await preparePartialRename(repository, index, file)
  const diff = await getWorkingDirectoryTextDiff(repository, index, file)

  if (!hasSelectedChange(file, diff)) {
    return false
  }

  const patch = formatPatch(file, diff)
  assertDiffSize(Buffer.byteLength(patch, 'utf8'), file.path)

  await git(
    ['apply', '--cached', '--unidiff-zero', '--whitespace=nowarn', '-'],
    repository.path,
    'applySelectedChangesReviewPatch',
    {
      env: index.env,
      stdin: patch,
    }
  )
  return true
}

async function resolveCommit(
  repository: Repository,
  commitish: string,
  operationName: string
): Promise<string | null> {
  const result = await git(
    ['rev-parse', '--verify', commitish],
    repository.path,
    operationName,
    { successExitCodes: new Set([0, 128]) }
  )
  if (result.exitCode !== 0) {
    return null
  }

  const objectId = result.stdout.trim()
  if (objectId.length === 0) {
    return null
  }

  const commitResult = await git(
    ['rev-parse', '--verify', `${objectId}^{commit}`],
    repository.path,
    operationName,
    { successExitCodes: new Set([0, 128]) }
  )
  if (commitResult.exitCode !== 0) {
    return null
  }

  const commitId = commitResult.stdout.trim()
  return commitId.length === 0 ? null : commitId
}

async function resolveReviewBaseTree(
  repository: Repository,
  commitish: string | undefined
): Promise<string> {
  const operationName = 'resolveSelectedChangesReviewBase'
  const requestedBase = commitish ?? 'HEAD'
  const commit = await resolveCommit(repository, requestedBase, operationName)

  if (commit !== null) {
    const treeResult = await git(
      ['rev-parse', '--verify', `${commit}^{tree}`],
      repository.path,
      operationName
    )
    return treeResult.stdout.trim()
  }

  if (commitish === undefined) {
    // An unborn HEAD has the empty tree as its stable base.
    return NullTreeSHA
  }

  // `rev-parse <root>^` fails for a parentless root commit. Treat that one
  // missing parent as the empty tree, matching amend's commit semantics.
  const parentMatch = /^(.*?)(?:\^|~1)$/.exec(commitish)
  if (parentMatch === null) {
    throw new SelectedChangesReviewSnapshotError(
      `Unable to resolve review base: ${commitish}`
    )
  }

  const parentBase = await resolveCommit(
    repository,
    parentMatch[1],
    operationName
  )
  if (parentBase === null) {
    throw new SelectedChangesReviewSnapshotError(
      `Unable to resolve review base: ${commitish}`
    )
  }

  const parentResult = await git(
    ['rev-parse', '--verify', `${parentBase}^`],
    repository.path,
    operationName,
    { successExitCodes: new Set([0, 128]) }
  )
  if (parentResult.exitCode !== 0) {
    return NullTreeSHA
  }

  const parentCommit = parentResult.stdout.trim()
  if (parentCommit.length === 0) {
    throw new SelectedChangesReviewSnapshotError(
      `Unable to resolve review base: ${commitish}`
    )
  }

  const treeResult = await git(
    ['rev-parse', '--verify', `${parentCommit}^{tree}`],
    repository.path,
    operationName
  )
  return treeResult.stdout.trim()
}

async function getFinalFileDiff(
  repository: Repository,
  index: TemporaryIndex,
  file: WorkingDirectoryFileChange,
  reviewBase: string
): Promise<ReadonlyArray<SnapshotFile>> {
  const pathspecs = [pathspec(file.path)]

  if (
    file.status.kind === AppFileStatusKind.Renamed ||
    file.status.kind === AppFileStatusKind.Copied
  ) {
    pathspecs.push(pathspec(file.status.oldPath))
  }

  const args = [
    'diff',
    '--no-ext-diff',
    '--no-textconv',
    '--patch',
    '--no-color',
    '--find-renames',
    '--find-copies',
    '--cached',
    reviewBase,
    '--',
    ...pathspecs,
  ]

  const output = await getBoundedDiffOutput(
    args,
    repository,
    'getSelectedChangesReviewSnapshotDiff',
    { env: index.env }
  )
  const diff = output.toString('utf8')
  const patches = splitDiffPatches(diff)
  const namesOutput = await getBoundedDiffOutput(
    [
      'diff',
      '--no-ext-diff',
      '--no-textconv',
      '--name-only',
      '-z',
      '--no-color',
      '--find-renames',
      '--find-copies',
      '--cached',
      reviewBase,
      '--',
      ...pathspecs,
    ],
    repository,
    'getSelectedChangesReviewSnapshotPaths',
    { env: index.env }
  )
  const paths = namesOutput
    .toString('utf8')
    .split('\0')
    .filter(path => path.length > 0)

  if (patches.length !== paths.length) {
    throw new SelectedChangesReviewSnapshotError(
      `Unable to map selected changes review diff for ${file.path}`
    )
  }

  return patches.map((patch, patchIndex) => {
    const patchPath = paths[patchIndex]
    if (patchPath === undefined) {
      throw new SelectedChangesReviewSnapshotError(
        `Unable to map selected changes review diff for ${file.path}`
      )
    }
    parseTextDiff(Buffer.from(patch, 'utf8'), patchPath, 'review snapshot diff')
    return { path: patchPath, diff: patch }
  })
}

function splitDiffPatches(diff: string): ReadonlyArray<string> {
  if (diff.length === 0) {
    return []
  }

  const starts = new Array<number>()
  const header = /^diff --git /gm
  let match: RegExpExecArray | null
  while ((match = header.exec(diff)) !== null) {
    starts.push(match.index)
  }

  if (starts.length <= 1) {
    return [diff]
  }

  return starts.map((start, index) =>
    diff.slice(start, starts[index + 1] ?? diff.length)
  )
}

export async function captureSelectedChangesReviewSnapshot(
  repository: Repository,
  files: ReadonlyArray<WorkingDirectoryFileChange>,
  commitish?: string
): Promise<ISelectedChangesReviewSnapshot> {
  const selectedFiles = files
    .filter(
      file => file.selection.getSelectionType() !== DiffSelectionType.None
    )
    .map(file => normalizeFile(repository, file))

  if (selectedFiles.length === 0) {
    return { diff: '', files: [] }
  }

  for (const file of selectedFiles) {
    if (file.status.submoduleStatus !== undefined) {
      throw new SelectedChangesReviewSnapshotError(
        `Selected changes review does not support submodules: ${file.path}`
      )
    }
    if (isUnsupportedFile(file)) {
      throw new SelectedChangesReviewSnapshotError(
        `Selected changes review does not support conflicted files: ${file.path}`
      )
    }
  }

  // Selection coordinates are based on the working-tree diff against HEAD,
  // even when an amend asks the final snapshot to include an older base.
  const headTree = await resolveReviewBaseTree(repository, undefined)
  const reviewBase =
    commitish === undefined
      ? headTree
      : await resolveReviewBaseTree(repository, commitish)
  const indexPath = getTempFilePath('selected-changes-review-index')

  try {
    const index = await initializeTemporaryIndex(
      repository,
      indexPath,
      headTree
    )
    await stageAllSelectedFiles(repository, index, selectedFiles)

    const filesWithChanges = new Set<string>()
    for (const file of selectedFiles) {
      if (file.selection.getSelectionType() === DiffSelectionType.Partial) {
        if (await applyPartialSelection(repository, index, file)) {
          filesWithChanges.add(file.id)
        }
      } else {
        filesWithChanges.add(file.id)
      }
    }

    const snapshotFiles = new Array<{ path: string; diff: string }>()
    const seenPaths = new Set<string>()
    let totalBytes = 0

    for (const file of selectedFiles) {
      if (!filesWithChanges.has(file.id)) {
        continue
      }

      const fileDiffs = await getFinalFileDiff(
        repository,
        index,
        file,
        reviewBase
      )
      for (const fileDiff of fileDiffs) {
        if (seenPaths.has(fileDiff.path)) {
          continue
        }
        seenPaths.add(fileDiff.path)
        const diffBytes = Buffer.byteLength(fileDiff.diff, 'utf8')
        totalBytes += diffBytes
        assertDiffSize(totalBytes)
        snapshotFiles.push(fileDiff)
      }
    }

    return {
      diff: snapshotFiles.map(file => file.diff).join(''),
      files: snapshotFiles,
    }
  } finally {
    await unlink(indexPath).catch(() => {})
    await unlink(`${indexPath}.lock`).catch(() => {})
  }
}
