import * as React from 'react'
import {
  ApplicableTheme,
  getApplicationThemeDefinition,
  getThemeClassNames,
} from './lib/application-theme'
import * as ipcRenderer from '../lib/ipc-renderer'

interface IAppThemeProps {
  readonly theme: ApplicableTheme
}

/**
 * A pseudo-component responsible for adding the applicable CSS
 * class names to the body tag in order to apply the currently
 * selected theme.
 *
 * This component is a PureComponent, meaning that it'll only
 * render when its props changes (shallow comparison).
 *
 * This component does not render anything into the DOM, it's
 * purely (a)busing the component lifecycle to manipulate the
 * body class list.
 */
export class AppTheme extends React.PureComponent<IAppThemeProps> {
  public componentDidMount() {
    this.ensureTheme()
  }

  public componentDidUpdate() {
    this.ensureTheme()
  }

  public componentWillUnmount() {
    this.clearThemes()
  }

  private ensureTheme() {
    const themeClassNames = getThemeClassNames(this.props.theme)
    const currentThemeClassNames = [...document.body.classList].filter(
      className => className.startsWith('theme-')
    )
    const hasExactThemeClasses =
      currentThemeClassNames.length === themeClassNames.length &&
      themeClassNames.every(className =>
        document.body.classList.contains(className)
      )

    if (!hasExactThemeClasses) {
      this.clearThemes()
      document.body.classList.add(...themeClassNames)
      this.updateColorScheme()
    }
  }

  private updateColorScheme = () => {
    const definition = getApplicationThemeDefinition(this.props.theme)
    const isDarkTheme =
      definition?.kind === 'fixed' && definition.nativeThemeSource === 'dark'
    const rootStyle = document.documentElement.style

    rootStyle.colorScheme = isDarkTheme ? 'dark' : 'light'

    // Update the window's background color to match the CSS value
    const backgroundColor = getComputedStyle(document.body).getPropertyValue(
      '--background-color'
    )
    if (backgroundColor) {
      ipcRenderer.send('update-window-background-color', backgroundColor.trim())
    }
  }

  private clearThemes() {
    const body = document.body

    // body.classList is a DOMTokenList and it does not iterate all the way
    // through with the for loop. (why it doesn't.. ¯\_(ツ)_/¯ - Possibly
    // because we are modifying it as we loop) Hence the extra step of
    // converting it to a string array.
    const classList = [...body.classList]
    for (const className of classList) {
      if (className.startsWith('theme-')) {
        body.classList.remove(className)
      }
    }
  }

  public render() {
    return null
  }
}
