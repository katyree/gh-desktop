/* eslint-disable no-sync */

import fs from 'fs'

import { expect, fakeCodexCapturePath, test } from './e2e-fixtures'
import { smokeRepoFileName, smokeRepoPath } from './test-helpers'

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
    const pathInput = existingDialog.locator(
      'input[placeholder="repository path"]'
    )
    await pathInput.fill(smokeRepoPath)
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

async function expectOptionValues(
  select: import('@playwright/test').Locator,
  values: ReadonlyArray<string>
) {
  const options = select.locator('option')
  await expect(options).toHaveCount(values.length)
  for (let index = 0; index < values.length; index++) {
    await expect(options.nth(index)).toHaveAttribute('value', values[index])
  }
}

test('selects a Codex model and reasoning effort for commit generation', async ({
  app,
  mainWindow: page,
}) => {
  test.skip(
    process.env.DESKTOP_E2E_FAKE_CODEX !== '1',
    'Run with DESKTOP_E2E_FAKE_CODEX=1.'
  )

  await finishWelcome(page)

  await page.bringToFront()
  await app.evaluate(({ Menu }) => {
    Menu.getApplicationMenu()?.getMenuItemById('preferences')?.click()
  })
  const options = page.locator('dialog#preferences')
  if (!(await options.isVisible({ timeout: 5_000 }).catch(() => false))) {
    await page.keyboard.press('Control+,')
  }
  await options.waitFor({ state: 'visible' })
  await page.getByRole('tab', { name: 'Codex' }).click()

  const model = options.getByLabel('Model', { exact: true })
  await expect(model).toBeVisible()
  await expect(model).toHaveValue('')
  await expectOptionValues(model, ['', 'fixture-balanced', 'fixture-deep'])

  const reasoning = options.getByLabel('Reasoning effort')
  await expect(reasoning).toHaveValue('')
  await expectOptionValues(reasoning, ['', 'low', 'medium'])

  await model.selectOption('fixture-deep')
  await expect(reasoning).toHaveValue('')
  await expectOptionValues(reasoning, ['', 'high', 'xhigh'])
  await reasoning.selectOption('xhigh')
  await expect(reasoning).toHaveValue('xhigh')

  await options.getByRole('button', { name: 'Close' }).click()
  await options.waitFor({ state: 'hidden' })
  await openSmokeRepository(page)

  const summary = page.getByLabel('Commit summary')
  await summary.fill('')
  const generate = page.getByRole('button', {
    name: 'Generate commit message with Codex',
  })
  await expect(generate).toBeEnabled()
  await generate.click()

  const consent = page.getByRole('alertdialog', {
    name: 'Share selected changes with OpenAI?',
  })
  await expect(consent).toBeVisible()
  await consent.getByRole('button', { name: 'Share selected changes' }).click()
  await expect(summary).toHaveValue('Describe selected change', {
    timeout: 15_000,
  })

  await expect
    .poll(
      () =>
        capturedMessages().filter(message => message.method === 'turn/start')
          .length,
      { timeout: 10_000 }
    )
    .toBeGreaterThanOrEqual(1)

  const messages = capturedMessages()
  const threadStart = messages.find(
    message => message.method === 'thread/start'
  ) as { params?: { model?: string } } | undefined
  const turnStart = messages.find(
    message => message.method === 'turn/start'
  ) as { params?: { effort?: string } } | undefined

  expect(threadStart?.params?.model).toBe('gpt-5.6-luna')
  expect(turnStart?.params?.effort).toBe('xhigh')
})
