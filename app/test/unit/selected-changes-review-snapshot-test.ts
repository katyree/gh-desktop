import assert from 'node:assert'
import { readFile, writeFile } from 'fs/promises'
import { describe, it } from 'node:test'
import { exec } from 'dugite'
import { join } from 'path'

import {
  DiffLineType,
  DiffSelection,
  DiffSelectionType,
  DiffType,
} from '../../src/models/diff'
import {
  AppFileStatusKind,
  WorkingDirectoryFileChange,
} from '../../src/models/status'
import { getWorkingDirectoryDiff, git } from '../../src/lib/git'
import { captureSelectedChangesReviewSnapshot } from '../../src/lib/selected-changes-review-snapshot'
import { setupEmptyRepository } from '../helpers/repositories'
import { makeCommit } from '../helpers/repository-scaffolding'
import { getStatusOrThrow } from '../helpers/status'

describe('selected changes review snapshot', () => {
  it('includes only selected changes and preserves repository state', async t => {
    const repository = await setupEmptyRepository(t)
    await makeCommit(repository, {
      entries: [
        {
          path: 'tracked.txt',
          contents: 'before one\nbefore two\nbefore three\nbefore four\n',
        },
        { path: 'unselected.txt', contents: 'unselected base\n' },
        { path: 'staged.txt', contents: 'staged base\n' },
      ],
    })

    await writeFile(
      join(repository.path, 'tracked.txt'),
      'before one\nselected addition\nbefore two\nunselected addition\nbefore three\nbefore four\n'
    )
    await writeFile(
      join(repository.path, 'unselected.txt'),
      'unselected change\n'
    )
    await writeFile(join(repository.path, 'staged.txt'), 'staged change\n')
    await exec(['add', 'staged.txt'], repository.path)

    const diffFile = new WorkingDirectoryFileChange(
      'tracked.txt',
      { kind: AppFileStatusKind.Modified },
      DiffSelection.fromInitialSelection(DiffSelectionType.None)
    )
    const workingDiff = await getWorkingDirectoryDiff(repository, diffFile)
    if (workingDiff.kind !== DiffType.Text) {
      throw new Error('Expected a text working directory diff')
    }

    let selectedLineIndex: number | undefined
    for (const hunk of workingDiff.hunks) {
      for (let lineIndex = 0; lineIndex < hunk.lines.length; lineIndex++) {
        const line = hunk.lines[lineIndex]
        if (
          line.type === DiffLineType.Add &&
          line.text === '+selected addition'
        ) {
          selectedLineIndex = hunk.unifiedDiffStart + lineIndex
        }
      }
    }
    if (selectedLineIndex === undefined) {
      throw new Error('Expected the selected addition in the working diff')
    }

    const selectedFile = new WorkingDirectoryFileChange(
      'tracked.txt',
      { kind: AppFileStatusKind.Modified },
      DiffSelection.fromInitialSelection(
        DiffSelectionType.None
      ).withLineSelection(selectedLineIndex, true)
    )
    const unselectedFile = new WorkingDirectoryFileChange(
      'unselected.txt',
      { kind: AppFileStatusKind.Modified },
      DiffSelection.fromInitialSelection(DiffSelectionType.None)
    )
    const stagedFile = new WorkingDirectoryFileChange(
      'staged.txt',
      { kind: AppFileStatusKind.Modified },
      DiffSelection.fromInitialSelection(DiffSelectionType.None)
    )

    const indexPath = join(repository.path, '.git', 'index')
    const beforeIndex = await readFile(indexPath)
    const beforeHead = (
      await git(
        ['rev-parse', 'HEAD'],
        repository.path,
        'selectedChangesReviewSnapshotTest'
      )
    ).stdout.trim()
    const beforeTracked = await readFile(join(repository.path, 'tracked.txt'))
    const beforeUnselected = await readFile(
      join(repository.path, 'unselected.txt')
    )
    const beforeStaged = await readFile(join(repository.path, 'staged.txt'))

    const snapshot = await captureSelectedChangesReviewSnapshot(repository, [
      selectedFile,
      unselectedFile,
      stagedFile,
    ])

    assert.equal(snapshot.files.length, 1)
    assert.equal(snapshot.files[0].path, 'tracked.txt')
    assert.equal(snapshot.diff, snapshot.files[0].diff)
    assert(snapshot.diff.includes('+selected addition'))
    assert(!snapshot.diff.includes('+unselected addition'))
    assert(!snapshot.diff.includes('unselected change'))
    assert(!snapshot.diff.includes('staged change'))

    assert.deepStrictEqual(await readFile(indexPath), beforeIndex)
    assert.equal(
      (
        await git(
          ['rev-parse', 'HEAD'],
          repository.path,
          'selectedChangesReviewSnapshotTest'
        )
      ).stdout.trim(),
      beforeHead
    )
    assert.deepStrictEqual(
      await readFile(join(repository.path, 'tracked.txt')),
      beforeTracked
    )
    assert.deepStrictEqual(
      await readFile(join(repository.path, 'unselected.txt')),
      beforeUnselected
    )
    assert.deepStrictEqual(
      await readFile(join(repository.path, 'staged.txt')),
      beforeStaged
    )
  })

  it('returns an empty snapshot when no changes are selected', async t => {
    const repository = await setupEmptyRepository(t)
    const file = new WorkingDirectoryFileChange(
      'unused.txt',
      { kind: AppFileStatusKind.Modified },
      DiffSelection.fromInitialSelection(DiffSelectionType.None)
    )

    assert.deepStrictEqual(
      await captureSelectedChangesReviewSnapshot(repository, [file]),
      { diff: '', files: [] }
    )
  })

  it('maps heavily rewritten rename patches to their exact paths', async t => {
    const repository = await setupEmptyRepository(t)
    await makeCommit(repository, {
      entries: [
        {
          path: 'original.txt',
          contents: Array.from(
            { length: 12 },
            (_, index) => `original line ${index + 1}\n`
          ).join(''),
        },
      ],
    })

    await exec(['mv', 'original.txt', 'renamed.txt'], repository.path)
    await writeFile(
      join(repository.path, 'renamed.txt'),
      Array.from(
        { length: 12 },
        (_, index) => `rewritten line ${index + 1}\n`
      ).join('')
    )

    const status = await getStatusOrThrow(repository)
    assert.equal(status.workingDirectory.files.length, 1)
    const renamedFile = status.workingDirectory.files[0]
    assert(renamedFile !== undefined)
    assert(renamedFile.status.kind === AppFileStatusKind.Renamed)
    assert.equal(renamedFile.status.oldPath, 'original.txt')

    const indexPath = join(repository.path, '.git', 'index')
    const beforeIndex = await readFile(indexPath)
    const snapshot = await captureSelectedChangesReviewSnapshot(repository, [
      renamedFile.withIncludeAll(true),
    ])

    assert.deepStrictEqual(snapshot.files.map(file => file.path).sort(), [
      'original.txt',
      'renamed.txt',
    ])
    const renamedPatch = snapshot.files.find(
      file => file.path === 'renamed.txt'
    )
    const originalPatch = snapshot.files.find(
      file => file.path === 'original.txt'
    )
    assert(renamedPatch !== undefined)
    assert(originalPatch !== undefined)
    assert(renamedPatch.diff.includes('+rewritten line 1'))
    assert(originalPatch.diff.includes('-original line 1'))
    assert(snapshot.files.every(file => file.diff.includes('diff --git')))
    assert.deepStrictEqual(await readFile(indexPath), beforeIndex)
  })
})
