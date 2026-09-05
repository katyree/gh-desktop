/* eslint-disable no-sync */

import fs from 'fs'
import path from 'path'

import {
  expect,
  fakeCodexCapturePath,
  fakeCodexControlPath,
  test,
} from './e2e-fixtures'
import {
  getSmokeRepoHeadMessage,
  getSmokeRepoStatus,
  smokeRepoFileContents,
  smokeRepoFileName,
  smokeRepoPath,
} from './test-helpers'

test.describe.configure({ mode: 'serial' })

async function finishWelcome(page: import('@playwright/test').Page) {
  await page.waitForFunction(
    () =>
      (document.getElementById('desktop-app-container')?.innerHTML.length ??
        0) > 100,
    null,
    { timeout: 30_000 }
  )
  const skip = page.locator('a.skip-button')
  if (await skip.isVisible().catch(() => false)) {
    await skip.click()
    await page.locator('input[placeholder="Your Name"]').fill('Test User')
    await page
      .locator('input[placeholder="your-email@example.com"]')
      .fill('test.user@example.invalid')
    await page.getByRole('button', { name: 'Finish' }).click()
    await page.locator('#welcome').waitFor({ state: 'hidden' })
  }
}

async function openSmokeRepository(page: import('@playwright/test').Page) {
  const repoFile = page
    .locator(`//*[contains(normalize-space(), "${smokeRepoFileName}")]`)
    .first()
  if (!(await repoFile.isVisible().catch(() => false))) {
    const existingDialog = page.locator('dialog#add-existing-repository')
    if (!(await existingDialog.isVisible().catch(() => false))) {
      await page
        .getByRole('button', {
          name: /Add an Existing Repository from your Local Drive/i,
        })
        .click()
    }
    await existingDialog.waitFor({ state: 'visible', timeout: 15_000 })
    await existingDialog
      .locator('input[placeholder="repository path"]')
      .fill(smokeRepoPath)
    await existingDialog
      .getByRole('button', { name: /Add repository/i })
      .click()
  }
  await repoFile.waitFor({ state: 'visible', timeout: 15_000 })
  await repoFile.click()
  await page.locator('.diff-container').waitFor({ state: 'visible' })
}

function capturedMessages() {
  if (!fs.existsSync(fakeCodexCapturePath)) {
    return []
  }
  return fs
    .readFileSync(fakeCodexCapturePath, 'utf8')
    .trim()
    .split(/\r?\n/)
    .filter(Boolean)
    .map(line => JSON.parse(line) as Record<string, unknown>)
}

function reviewTurnStarts() {
  return capturedMessages().filter(message => {
    const params = message.params as { outputSchema?: unknown } | undefined
    const outputSchema = params?.outputSchema as
      | { properties?: { findings?: unknown } }
      | undefined
    return outputSchema?.properties?.findings !== undefined
  })
}

async function acceptReviewConsent(page: import('@playwright/test').Page) {
  const consent = page.getByRole('alertdialog', {
    name: 'Share selected changes with OpenAI?',
  })
  if (await consent.isVisible().catch(() => false)) {
    await consent
      .getByRole('button', { name: 'Share selected changes' })
      .click()
  }
}

function reviewButton(page: import('@playwright/test').Page) {
  return page.getByRole('button', {
    name: /Review selected changes with Codex/i,
  })
}

function reviewDialog(page: import('@playwright/test').Page) {
  return page.locator('dialog#selected-changes-review')
}

function findingTitle(page: import('@playwright/test').Page) {
  return reviewDialog(page).getByText('Check the selected value', {
    exact: true,
  })
}

function findingLocation(page: import('@playwright/test').Page) {
  return reviewDialog(page).getByRole('button', {
    name: /Open reviewed diff for smoke-change\.txt, line 1, new side/i,
  })
}

test('reviews only selected changes and invalidates stale findings', async ({
  mainWindow: page,
}) => {
  test.skip(
    process.env.DESKTOP_E2E_FAKE_CODEX !== '1',
    'Run with DESKTOP_E2E_FAKE_CODEX=1.'
  )
  await finishWelcome(page)
  await openSmokeRepository(page)

  const unselectedFile = page
    .locator('.file')
    .filter({ hasText: 'unselected-secret.txt' })
  await unselectedFile.waitFor({ state: 'visible', timeout: 15_000 })
  await unselectedFile
    .locator('input[type="checkbox"]')
    .uncheck({ force: true })

  const selectedFile = page
    .locator('.file')
    .filter({ hasText: smokeRepoFileName })
  const selectedCheckbox = selectedFile.locator('input[type="checkbox"]')
  const summary = page.getByLabel('Commit summary')
  const description = page.getByLabel('Commit description')
  await summary.fill('Keep this draft summary')
  await description.fill('Keep this draft description')
  const initialHead = getSmokeRepoHeadMessage()

  const review = reviewButton(page)
  await expect(review).toBeEnabled()
  await review.click()
  await acceptReviewConsent(page)

  const finding = findingTitle(page)
  await expect(finding).toBeVisible({ timeout: 15_000 })
  const reviewStarts = reviewTurnStarts()
  expect(reviewStarts.length).toBeGreaterThan(0)
  const threadStart = capturedMessages()
    .filter(message => message.method === 'thread/start')
    .at(-1)
  const threadParams = threadStart?.params as
    | {
        sandbox?: unknown
        ephemeral?: unknown
        dynamicTools?: unknown
      }
    | undefined
  expect(threadParams?.sandbox).toBe('read-only')
  expect(threadParams?.ephemeral).toBe(true)
  expect(threadParams?.dynamicTools).toEqual([])
  const prompt = (
    reviewStarts.at(-1)?.params as
      | { input?: Array<{ text?: string }> }
      | undefined
  )?.input?.[0]?.text
  expect(prompt).toContain(smokeRepoFileContents)
  expect(prompt).not.toContain('UNSELECTED_PRIVATE_CONTENT')

  const location = findingLocation(page)
  await expect(location).toHaveText('smoke-change.txt:1')
  await location.click()
  await expect(
    reviewDialog(page).locator('.selected-changes-review-diff-row.is-focused')
  ).toBeVisible()

  await reviewDialog(page).locator('button.close').click()
  await expect(reviewDialog(page)).not.toBeVisible()

  await selectedCheckbox.uncheck({ force: true })
  await selectedCheckbox.check({ force: true })
  await expect(review).toBeEnabled()
  await review.click()
  await expect(
    reviewDialog(page).getByText('Outdated review', { exact: true })
  ).toBeVisible({ timeout: 15_000 })
  await expect(
    reviewDialog(page).getByText('The selected changes have changed.')
  ).toBeVisible()
  await reviewDialog(page)
    .getByRole('button', { name: 'Review again', exact: true })
    .click()
  await acceptReviewConsent(page)
  await expect(finding).toBeVisible({ timeout: 15_000 })

  const reviewStartsBeforeContent = reviewTurnStarts().length
  fs.appendFileSync(
    path.join(smokeRepoPath, smokeRepoFileName),
    'Changed after the review snapshot.\n'
  )

  await reviewDialog(page).locator('button.close').click()
  await expect(reviewDialog(page)).not.toBeVisible()
  await review.click()
  await expect(
    reviewDialog(page).getByText('Outdated review', { exact: true })
  ).toBeVisible({ timeout: 15_000 })
  await expect(
    reviewDialog(page).getByText('The selected file contents have changed.')
  ).toBeVisible()
  await reviewDialog(page)
    .getByRole('button', { name: 'Review again', exact: true })
    .click()
  await acceptReviewConsent(page)
  await expect
    .poll(() => reviewTurnStarts().length, { timeout: 15_000 })
    .toBe(reviewStartsBeforeContent + 1)
  await expect(finding).toBeVisible({ timeout: 15_000 })
  const contentReviewPrompt = (
    reviewTurnStarts().at(-1)?.params as
      | { input?: Array<{ text?: string }> }
      | undefined
  )?.input?.[0]?.text
  expect(contentReviewPrompt).toContain('Changed after the review snapshot.')

  await expect(summary).toHaveValue('Keep this draft summary')
  await expect(description).toHaveValue('Keep this draft description')
  expect(getSmokeRepoHeadMessage()).toBe(initialHead)
  expect(getSmokeRepoStatus()).not.toBe('')

  await findingLocation(page).click()
  await expect(
    reviewDialog(page).locator('.selected-changes-review-diff-row.is-focused')
  ).toBeVisible()
  await page.screenshot({
    path: path.resolve(__dirname, '../../../selected-changes-review-e2e.png'),
  })

  await reviewDialog(page).locator('button.close').click()
  await expect(reviewDialog(page)).not.toBeVisible()
})

test('cancels a slow selected-changes review without changing the draft', async ({
  mainWindow: page,
}) => {
  test.skip(process.env.DESKTOP_E2E_FAKE_CODEX !== '1')
  try {
    fs.writeFileSync(fakeCodexControlPath, 'slow')
    const review = reviewButton(page)
    const generationsBefore = reviewTurnStarts().length
    await review.click()
    await expect(reviewDialog(page)).toBeVisible()
    await reviewDialog(page)
      .getByRole('button', { name: 'Review again', exact: true })
      .click()

    const cancel = reviewDialog(page).getByRole('button', {
      name: 'Cancel selected-changes review',
      exact: true,
    })
    await expect(cancel).toBeVisible({ timeout: 15_000 })
    await expect
      .poll(() => reviewTurnStarts().length, { timeout: 10_000 })
      .toBe(generationsBefore + 1)
    await cancel.click()

    await expect(reviewDialog(page)).toBeVisible()
    await expect(
      reviewDialog(page).getByText(
        'Select changes to review them with Codex.',
        {
          exact: true,
        }
      )
    ).toBeVisible()
    await expect(page.locator('dialog#app-error')).not.toBeVisible()
    await expect
      .poll(
        () =>
          capturedMessages().some(
            message => message.method === 'turn/interrupt'
          ),
        { timeout: 10_000 }
      )
      .toBe(true)
    await expect(page.getByLabel('Commit summary')).toHaveValue(
      'Keep this draft summary'
    )
    await expect(page.getByLabel('Commit description')).toHaveValue(
      'Keep this draft description'
    )
    await reviewDialog(page).locator('button.close').click()
    await expect(reviewDialog(page)).not.toBeVisible()
  } finally {
    fs.writeFileSync(fakeCodexControlPath, 'success')
  }
})
