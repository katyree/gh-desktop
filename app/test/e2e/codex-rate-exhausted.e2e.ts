import { expect, test } from './e2e-fixtures'
import { smokeRepoFileName, smokeRepoPath } from './test-helpers'

test('disables local commit generation when App Server reports exhaustion', async ({
  mainWindow: page,
}) => {
  test.skip(
    process.env.DESKTOP_E2E_FAKE_CODEX !== '1' ||
      process.env.DESKTOP_E2E_CODEX_INITIAL_MODE !== 'exhausted',
    'Run with the deterministic Codex fixture in exhausted mode.'
  )
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

  const repoFile = page
    .locator(`//*[contains(normalize-space(), "${smokeRepoFileName}")]`)
    .first()
  if (!(await repoFile.isVisible().catch(() => false))) {
    const addDialog = page.locator('dialog#add-existing-repository')
    if (!(await addDialog.isVisible().catch(() => false))) {
      await page
        .getByRole('button', {
          name: /Add an Existing Repository from your Local Drive/i,
        })
        .click()
    }
    await addDialog.waitFor({ state: 'visible' })
    await addDialog
      .locator('input[placeholder="repository path"]')
      .fill(smokeRepoPath)
    await addDialog.getByRole('button', { name: /Add repository/i }).click()
  }
  await repoFile.waitFor({ state: 'visible', timeout: 15_000 })
  await repoFile.click()

  const button = page.getByRole('button', {
    name: /ChatGPT usage is exhausted/,
  })
  await expect(button).toBeVisible({ timeout: 15_000 })
  await expect(button).toBeDisabled()
})
