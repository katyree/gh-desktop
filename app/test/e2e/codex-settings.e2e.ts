import { test, expect } from './e2e-fixtures'

test('opens Codex settings and reaches every action by keyboard', async ({
  app,
  mainWindow: page,
}) => {
  await page.waitForFunction(
    () =>
      (document.getElementById('desktop-app-container')?.innerHTML.length ??
        0) > 100,
    null,
    { timeout: 30000 }
  )

  const skipButton = page.locator('a.skip-button')
  if (await skipButton.isVisible().catch(() => false)) {
    await skipButton.click()

    const nameInput = page.locator('input[placeholder="Your Name"]')
    await nameInput.fill('Test User')
    await page
      .locator('input[placeholder="your-email@example.com"]')
      .fill('test.user@example.com')
    await page.getByRole('button', { name: 'Finish' }).click()
    await page.locator('#welcome').waitFor({ state: 'hidden' })
  }

  await app.evaluate(({ Menu }) => {
    Menu.getApplicationMenu()?.getMenuItemById('preferences')?.click()
  })
  const options = page.locator('dialog#preferences')
  await options.waitFor({ state: 'visible' })

  // Move backward from the dialog's preferred action until the selected tab
  // receives focus. Arrow keys then use the tab list's native keyboard path.
  const accountsTab = page.getByRole('tab', { name: 'Accounts' })
  for (let index = 0; index < 12; index++) {
    if (
      await accountsTab.evaluate(element => element === document.activeElement)
    ) {
      break
    }
    await page.keyboard.press('Shift+Tab')
  }
  await expect(accountsTab).toBeFocused()
  await expect(accountsTab).toHaveAttribute('aria-selected', 'true')
  for (let index = 0; index < 7; index++) {
    await page.keyboard.press('ArrowUp')
  }

  const codexTab = page.getByRole('tab', { name: 'Codex' })
  await expect(codexTab).toHaveAttribute('aria-selected', 'true')
  await expect(codexTab).toBeFocused()
  await expect(
    page.getByRole('heading', { name: 'ChatGPT account' })
  ).toBeVisible()

  await page.keyboard.press('Tab')
  await expect(
    page.getByRole('button', { name: 'Sign in with ChatGPT' })
  ).toBeFocused()
  await page.keyboard.press('Tab')
  await expect(
    page.getByRole('button', { name: 'Use a device code' })
  ).toBeFocused()

  await expect(options).not.toContainText('Copilot license')
  await expect(options).not.toContainText('View Copilot plans')
})
