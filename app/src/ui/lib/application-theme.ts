import {
  isMacOSMojaveOrLater,
  isWindows10And1809Preview17666OrLater,
} from '../../lib/get-os'
import { getBoolean } from '../../lib/local-storage'
import {
  setNativeThemeSource,
  shouldUseDarkColors,
} from '../main-process-proxy'
import { ThemeSource } from './theme-source'

/**
 * A set of the user-selectable appearances (aka themes)
 */
export enum ApplicationTheme {
  Light = 'light',
  Dark = 'dark',
  System = 'system',
  Nord = 'nord',
  Amoled = 'amoled',
  MonokaiPro = 'monokai-pro',
  OneDark = 'one-dark',
}

export type FixedApplicationTheme = Exclude<
  ApplicationTheme,
  ApplicationTheme.System
>

export type ApplicableTheme = FixedApplicationTheme

type NativeThemeSource = Exclude<ThemeSource, 'system'>

interface FixedApplicationThemeDefinition {
  readonly kind: 'fixed'
  readonly theme: FixedApplicationTheme
  readonly label: string
  readonly preview: string
  readonly nativeThemeSource: NativeThemeSource
  readonly classNames: ReadonlyArray<string>
}

interface SystemApplicationThemeDefinition {
  readonly kind: 'system'
  readonly theme: ApplicationTheme.System
  readonly label: string
  readonly preview: {
    readonly light: string
    readonly dark: string
  }
}

export type ApplicationThemeDefinition =
  | FixedApplicationThemeDefinition
  | SystemApplicationThemeDefinition

/** The complete set of themes shown in the appearance preferences. */
export const applicationThemes: ReadonlyArray<ApplicationThemeDefinition> = [
  {
    kind: 'fixed',
    theme: ApplicationTheme.Light,
    label: 'Light',
    preview: 'ghd_light.svg',
    nativeThemeSource: 'light',
    classNames: ['theme-light'],
  },
  {
    kind: 'fixed',
    theme: ApplicationTheme.Dark,
    label: 'Dark',
    preview: 'ghd_dark.svg',
    nativeThemeSource: 'dark',
    classNames: ['theme-dark'],
  },
  {
    kind: 'system',
    theme: ApplicationTheme.System,
    label: 'System',
    preview: {
      light: 'ghd_light.svg',
      dark: 'ghd_dark.svg',
    },
  },
  {
    kind: 'fixed',
    theme: ApplicationTheme.Nord,
    label: 'Nord',
    preview: 'ghd_nord.svg',
    nativeThemeSource: 'dark',
    classNames: ['theme-dark', 'theme-nord'],
  },
  {
    kind: 'fixed',
    theme: ApplicationTheme.Amoled,
    label: 'AMOLED',
    preview: 'ghd_amoled.svg',
    nativeThemeSource: 'dark',
    classNames: ['theme-dark', 'theme-amoled'],
  },
  {
    kind: 'fixed',
    theme: ApplicationTheme.MonokaiPro,
    label: 'Monokai Pro (CE)',
    preview: 'ghd_monokai_pro.svg',
    nativeThemeSource: 'dark',
    classNames: ['theme-dark', 'theme-monokai-pro'],
  },
  {
    kind: 'fixed',
    theme: ApplicationTheme.OneDark,
    label: 'One Dark',
    preview: 'ghd_one_dark.svg',
    nativeThemeSource: 'dark',
    classNames: ['theme-dark', 'theme-one-dark'],
  },
]

export function getApplicationThemeDefinition(
  theme: ApplicationTheme
): ApplicationThemeDefinition
export function getApplicationThemeDefinition(
  theme: unknown
): ApplicationThemeDefinition | undefined
export function getApplicationThemeDefinition(
  theme: unknown
): ApplicationThemeDefinition | undefined {
  return applicationThemes.find(definition => definition.theme === theme)
}

/** Return a valid persisted theme, defaulting to System for unknown values. */
export function normalizeApplicationTheme(theme: unknown): ApplicationTheme {
  return getApplicationThemeDefinition(theme)?.theme ?? ApplicationTheme.System
}

export function getThemeClassNames(
  theme: FixedApplicationTheme
): ReadonlyArray<string> {
  const definition = getApplicationThemeDefinition(theme)
  return definition.kind === 'fixed' ? definition.classNames : []
}

/**
 * Gets the friendly name of an application theme for use
 * in persisting to storage and/or calculating the required
 * body class name to set in order to apply the theme.
 */
export function getThemeName(theme: ApplicationTheme): ThemeSource {
  const definition = getApplicationThemeDefinition(theme)
  return definition.kind === 'fixed' ? definition.nativeThemeSource : 'system'
}

// The key under which the decision to automatically switch the theme is persisted
// in localStorage.
const automaticallySwitchApplicationThemeKey = 'autoSwitchTheme'

/**
 * Function to preserve and convert legacy theme settings
 * should be removable after most users have upgraded to 2.7.0+
 */
function migrateAutomaticallySwitchSetting(): string | null {
  const automaticallySwitchApplicationTheme = getBoolean(
    automaticallySwitchApplicationThemeKey,
    false
  )

  localStorage.removeItem(automaticallySwitchApplicationThemeKey)

  if (automaticallySwitchApplicationTheme) {
    setPersistedTheme(ApplicationTheme.System)
    return 'system'
  }

  return null
}

// The key under which the currently selected theme is persisted
// in localStorage.
const applicationThemeKey = 'theme'

/**
 * Returns User's theme preference or 'system' if not set or parsable
 */
function getApplicationThemeSetting(): ApplicationTheme {
  return normalizeApplicationTheme(localStorage.getItem(applicationThemeKey))
}

/**
 * Load the name of the currently selected theme
 */
export async function getCurrentlyAppliedTheme(): Promise<ApplicableTheme> {
  return (await isDarkModeEnabled())
    ? ApplicationTheme.Dark
    : ApplicationTheme.Light
}

/**
 * Load the name of the currently selected theme
 */
export function getPersistedThemeName(): ApplicationTheme {
  if (migrateAutomaticallySwitchSetting() === 'system') {
    return ApplicationTheme.System
  }

  return getApplicationThemeSetting()
}

/**
 * Stores the given theme in the persistent store.
 */
export function setPersistedTheme(theme: ApplicationTheme): void {
  const normalizedTheme = normalizeApplicationTheme(theme)
  const themeName = getThemeName(normalizedTheme)
  localStorage.setItem(applicationThemeKey, normalizedTheme)
  setNativeThemeSource(themeName)
}

/**
 * Whether or not the current OS supports System Theme Changes
 */
export function supportsSystemThemeChanges(): boolean {
  if (__DARWIN__) {
    return isMacOSMojaveOrLater()
  } else if (__WIN32__) {
    // Its technically possible this would still work on prior versions of Windows 10 but 1809
    // was released October 2nd, 2018 and the feature can just be "attained" by upgrading
    // See https://github.com/desktop/desktop/issues/9015 for more
    return isWindows10And1809Preview17666OrLater()
  } else {
    // enabling this for Linux users as an experiment to see if distributions
    // work with how Chromium detects theme changes
    return true
  }
}

function isDarkModeEnabled(): Promise<boolean> {
  return shouldUseDarkColors()
}
