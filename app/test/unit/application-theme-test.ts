import assert from 'node:assert'
import { afterEach, describe, it } from 'node:test'

import {
  ApplicationTheme,
  FixedApplicationTheme,
  applicationThemes,
  getApplicationThemeDefinition,
  getPersistedThemeName,
  getThemeClassNames,
  getThemeName,
  normalizeApplicationTheme,
} from '../../src/ui/lib/application-theme'

afterEach(() => {
  localStorage.clear()
})

describe('application themes', () => {
  it('registers every theme exactly once', () => {
    const registeredThemes = applicationThemes.map(
      definition => definition.theme
    )

    assert.deepEqual(registeredThemes, Object.values(ApplicationTheme))
    assert.equal(new Set(registeredThemes).size, registeredThemes.length)
  })

  it('maps every bundled palette to dark native controls and a distinct class', () => {
    const bundledThemes: ReadonlyArray<FixedApplicationTheme> = [
      ApplicationTheme.Nord,
      ApplicationTheme.Amoled,
      ApplicationTheme.MonokaiPro,
      ApplicationTheme.OneDark,
    ]

    for (const theme of bundledThemes) {
      const definition = getApplicationThemeDefinition(theme)

      assert.equal(definition?.kind, 'fixed')
      assert.equal(getThemeName(theme), 'dark')
      assert.ok(getThemeClassNames(theme).includes('theme-dark'))
      assert.ok(getThemeClassNames(theme).includes(`theme-${theme}`))
    }
  })

  it('loads registered persisted themes and falls back to System for unknown values', () => {
    localStorage.setItem('theme', ApplicationTheme.Nord)
    assert.equal(getPersistedThemeName(), ApplicationTheme.Nord)

    localStorage.setItem('theme', 'future-theme')
    assert.equal(getPersistedThemeName(), ApplicationTheme.System)
    assert.equal(normalizeApplicationTheme(undefined), ApplicationTheme.System)
  })
})
