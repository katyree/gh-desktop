import { resolve } from 'path'

interface IColor {
  readonly red: number
  readonly green: number
  readonly blue: number
  readonly alpha: number
}

interface ISassResult {
  readonly css: Buffer
}

interface ISass {
  render(
    options: {
      readonly data: string
      readonly includePaths: ReadonlyArray<string>
      readonly outputStyle: 'expanded'
    },
    callback: (error: Error | null, result: ISassResult) => void
  ): void
}

interface IThemeProperties {
  readonly [property: string]: string
}

interface IContrastCheck {
  readonly state: string
  readonly foreground: string
  readonly background: string
  readonly backgroundBase?: IColor
  readonly threshold: number
}

interface IFailure {
  readonly theme: string
  readonly state: string
  readonly ratio: number
  readonly threshold: number
  readonly foreground: string
  readonly background: string
}

const sass: ISass = require('sass')

const textContrastThreshold = 4.5
const nonTextContrastThreshold = 3

/**
 * These are the nine custom palettes registered in application-theme.ts.
 * Their values are read by Sass below; this list contains no color fixtures.
 */
const customThemeSources = [
  'nord',
  'amoled',
  'monokai-pro',
  'one-dark',
  'dracula',
  'tokyo-night',
  'catppuccin-mocha',
  'gruvbox-dark',
  'graphite',
]

const projectRoot = resolve(__dirname, '..')
const stylesRoot = resolve(projectRoot, 'app', 'styles')

function parseColor(rawColor: string): IColor {
  const value = rawColor.trim().toLowerCase()

  if (value.startsWith('#')) {
    let hex = value.slice(1)
    if (hex.length === 3 || hex.length === 4) {
      hex = hex
        .split('')
        .map(channel => channel + channel)
        .join('')
    }

    if (hex.length !== 6 && hex.length !== 8) {
      throw new Error(`Unsupported hexadecimal color: ${rawColor}`)
    }

    return {
      red: parseInt(hex.slice(0, 2), 16),
      green: parseInt(hex.slice(2, 4), 16),
      blue: parseInt(hex.slice(4, 6), 16),
      alpha: hex.length === 8 ? parseInt(hex.slice(6, 8), 16) / 255 : 1,
    }
  }

  if (value === 'white') {
    return { red: 255, green: 255, blue: 255, alpha: 1 }
  }

  if (value.startsWith('rgb')) {
    const openingParenthesis = value.indexOf('(')
    const channels = value
      .slice(openingParenthesis + 1, -1)
      .split(',')
      .map(channel => channel.trim())

    if (channels.length !== 3 && channels.length !== 4) {
      throw new Error(`Unsupported RGB color: ${rawColor}`)
    }

    return {
      red: parseFloat(channels[0]),
      green: parseFloat(channels[1]),
      blue: parseFloat(channels[2]),
      alpha: channels.length === 4 ? parseFloat(channels[3]) : 1,
    }
  }

  throw new Error(`Unsupported color: ${rawColor}`)
}

function composite(foreground: IColor, background: IColor): IColor {
  const alpha = foreground.alpha + background.alpha * (1 - foreground.alpha)

  if (alpha === 0) {
    return { red: 0, green: 0, blue: 0, alpha: 0 }
  }

  return {
    red:
      (foreground.red * foreground.alpha +
        background.red * background.alpha * (1 - foreground.alpha)) /
      alpha,
    green:
      (foreground.green * foreground.alpha +
        background.green * background.alpha * (1 - foreground.alpha)) /
      alpha,
    blue:
      (foreground.blue * foreground.alpha +
        background.blue * background.alpha * (1 - foreground.alpha)) /
      alpha,
    alpha,
  }
}

function relativeLuminance(color: IColor) {
  const linearize = (channel: number) => {
    const normalized = channel / 255
    return normalized <= 0.03928
      ? normalized / 12.92
      : Math.pow((normalized + 0.055) / 1.055, 2.4)
  }

  return (
    0.2126 * linearize(color.red) +
    0.7152 * linearize(color.green) +
    0.0722 * linearize(color.blue)
  )
}

function contrastRatio(first: IColor, second: IColor) {
  const firstLuminance = relativeLuminance(first)
  const secondLuminance = relativeLuminance(second)
  return (
    (Math.max(firstLuminance, secondLuminance) + 0.05) /
    (Math.min(firstLuminance, secondLuminance) + 0.05)
  )
}

function extractSelectorBlock(css: string, selector: string) {
  const selectorStart = css.indexOf(`${selector} {`)
  if (selectorStart < 0) {
    throw new Error(`Sass output did not contain ${selector}`)
  }

  const openingBrace = css.indexOf('{', selectorStart)
  let braceDepth = 0

  for (let index = openingBrace; index < css.length; index++) {
    if (css[index] === '{') {
      braceDepth++
    } else if (css[index] === '}') {
      braceDepth--
      if (braceDepth === 0) {
        return css.slice(openingBrace + 1, index)
      }
    }
  }

  throw new Error(`Sass output contained an unterminated ${selector} block`)
}

async function extractThemeProperties(
  themeSource: string
): Promise<IThemeProperties> {
  const result = await new Promise<ISassResult>((resolveResult, reject) => {
    sass.render(
      {
        data: `@import 'themes/custom-dark'; @import 'themes/${themeSource}';`,
        includePaths: [stylesRoot],
        outputStyle: 'expanded',
      },
      (error, rendered) => {
        if (error !== null) {
          reject(error)
          return
        }

        resolveResult(rendered)
      }
    )
  })
  const css = result.css.toString()
  const block = extractSelectorBlock(
    css,
    `body.theme-custom-dark.theme-${themeSource}`
  )
  const properties: { [property: string]: string } = {}

  for (const declaration of block.split(';')) {
    const separator = declaration.indexOf(':')
    if (separator < 0) {
      continue
    }

    const property = declaration.slice(0, separator).trim()
    if (!property.startsWith('--')) {
      continue
    }

    properties[property.slice(2)] = declaration.slice(separator + 1).trim()
  }

  return properties
}

function property(properties: IThemeProperties, name: string) {
  const value = properties[name]
  if (value === undefined) {
    throw new Error(`Theme is missing --${name}`)
  }
  if (value.startsWith('var(')) {
    throw new Error(`Theme property --${name} was not resolved by Sass`)
  }
  return parseColor(value)
}

function effectiveBackground(
  properties: IThemeProperties,
  backgroundProperty: string,
  base: IColor
) {
  return composite(property(properties, backgroundProperty), base)
}

function createContrastChecks(
  properties: IThemeProperties
): ReadonlyArray<IContrastCheck> {
  const diffBackground = property(properties, 'diff-background-color')
  const addBackground = effectiveBackground(
    properties,
    'diff-add-background-color',
    diffBackground
  )
  const deleteBackground = effectiveBackground(
    properties,
    'diff-delete-background-color',
    diffBackground
  )
  const gutterBackground = effectiveBackground(
    properties,
    'diff-gutter-background-color',
    diffBackground
  )
  const addGutterBackground = effectiveBackground(
    properties,
    'diff-add-gutter-background-color',
    addBackground
  )
  const deleteGutterBackground = effectiveBackground(
    properties,
    'diff-delete-gutter-background-color',
    deleteBackground
  )
  const hunkBackground = effectiveBackground(
    properties,
    'diff-hunk-background-color',
    diffBackground
  )
  return [
    {
      state: 'inner add word highlight',
      foreground: 'diff-add-text-color',
      background: 'diff-add-inner-background-color',
      threshold: textContrastThreshold,
    },
    {
      state: 'inner delete word highlight',
      foreground: 'diff-delete-text-color',
      background: 'diff-delete-inner-background-color',
      threshold: textContrastThreshold,
    },
    {
      state: 'selected added row',
      foreground: 'diff-selected-text-color',
      background: 'diff-selected-background-color',
      backgroundBase: addGutterBackground,
      threshold: textContrastThreshold,
    },
    {
      state: 'selected deleted row',
      foreground: 'diff-selected-text-color',
      background: 'diff-selected-background-color',
      backgroundBase: deleteGutterBackground,
      threshold: textContrastThreshold,
    },
    {
      state: 'hover context row',
      foreground: 'diff-hover-text-color',
      background: 'diff-hover-background-color',
      backgroundBase: gutterBackground,
      threshold: textContrastThreshold,
    },
    {
      state: 'hover added row',
      foreground: 'diff-add-hover-text-color',
      background: 'diff-add-hover-background-color',
      backgroundBase: addGutterBackground,
      threshold: textContrastThreshold,
    },
    {
      state: 'hover deleted row',
      foreground: 'diff-delete-hover-text-color',
      background: 'diff-delete-hover-background-color',
      backgroundBase: deleteGutterBackground,
      threshold: textContrastThreshold,
    },
    {
      state: 'hunk expansion handle text',
      foreground: 'diff-hunk-text-color',
      background: 'diff-hunk-gutter-background-color',
      backgroundBase: hunkBackground,
      threshold: textContrastThreshold,
    },
    {
      state: 'empty hunk handle on empty row',
      foreground: 'diff-empty-hunk-handle',
      background: 'diff-empty-row-background-color',
      backgroundBase: diffBackground,
      threshold: nonTextContrastThreshold,
    },
    {
      state: 'empty hunk handle on added row',
      foreground: 'diff-empty-hunk-handle',
      background: 'diff-add-background-color',
      backgroundBase: diffBackground,
      threshold: nonTextContrastThreshold,
    },
    {
      state: 'empty hunk handle on deleted row',
      foreground: 'diff-empty-hunk-handle',
      background: 'diff-delete-background-color',
      backgroundBase: diffBackground,
      threshold: nonTextContrastThreshold,
    },
    {
      state: 'selected hunk handle on empty row',
      foreground: 'diff-selected-border-color',
      background: 'diff-empty-row-background-color',
      backgroundBase: diffBackground,
      threshold: nonTextContrastThreshold,
    },
    {
      state: 'selected hunk handle on added row',
      foreground: 'diff-selected-border-color',
      background: 'diff-add-background-color',
      backgroundBase: diffBackground,
      threshold: nonTextContrastThreshold,
    },
    {
      state: 'selected hunk handle on deleted row',
      foreground: 'diff-selected-border-color',
      background: 'diff-delete-background-color',
      backgroundBase: diffBackground,
      threshold: nonTextContrastThreshold,
    },
    {
      state: 'empty hunk handle icon text',
      foreground: 'diff-empty-hunk-handle-foreground-color',
      background: 'diff-empty-hunk-handle',
      threshold: textContrastThreshold,
    },
    {
      state: 'selected hunk handle icon text',
      foreground: 'diff-selected-hunk-handle-foreground-color',
      background: 'diff-selected-border-color',
      threshold: textContrastThreshold,
    },
  ]
}

function effectiveColor(
  properties: IThemeProperties,
  colorProperty: string,
  backgroundBase?: IColor
) {
  const color = property(properties, colorProperty)
  return backgroundBase === undefined ? color : composite(color, backgroundBase)
}

async function main() {
  const failures: Array<IFailure> = []

  for (const theme of customThemeSources) {
    const properties = await extractThemeProperties(theme)

    for (const check of createContrastChecks(properties)) {
      const foreground = effectiveColor(properties, check.foreground)
      const background = effectiveColor(
        properties,
        check.background,
        check.backgroundBase
      )
      const ratio = contrastRatio(foreground, background)

      if (ratio < check.threshold) {
        failures.push({
          theme,
          state: check.state,
          ratio,
          threshold: check.threshold,
          foreground: check.foreground,
          background: check.background,
        })
      }
    }
  }

  if (failures.length > 0) {
    console.error(
      `Theme contrast check failed: ${failures.length} state${
        failures.length === 1 ? '' : 's'
      } below the required ratio`
    )
    for (const failure of failures) {
      console.error(
        `- ${failure.theme} / ${failure.state}: ${failure.ratio.toFixed(
          2
        )}:1 (required ${failure.threshold.toFixed(1)}:1, ${
          failure.foreground
        } on ${failure.background})`
      )
    }
    process.exitCode = 1
    return
  }

  console.log(
    `Theme contrast check passed for ${customThemeSources.length} custom themes`
  )
}

main().catch(error => {
  console.error(error)
  process.exitCode = 1
})
