export interface IReleaseEnvironment {
  readonly DESKTOP_E2E_UPDATES_URL?: string
  readonly WINGIT_UPDATES_URL?: string
  readonly WINGIT_ENABLE_UPDATES?: string
  readonly WINGIT_ENABLE_AUTOMATIC_UPDATES?: string
  readonly WINGIT_AZURE_SIGNING_ENDPOINT?: string
  readonly WINGIT_AZURE_SIGNING_ACCOUNT?: string
  readonly WINGIT_AZURE_SIGNING_PROFILE?: string
}

export interface IWinGitSigningMetadata {
  readonly Endpoint: string
  readonly CodeSigningAccountName: string
  readonly CertificateProfileName: string
}

function requireHTTPSURL(value: string, name: string) {
  const url = new URL(value)
  if (url.protocol !== 'https:') {
    throw new Error(`${name} must use HTTPS`)
  }
  return url.toString()
}

export function getConfiguredUpdatesURL(
  environment: IReleaseEnvironment = process.env as IReleaseEnvironment
): string | undefined {
  if (environment.DESKTOP_E2E_UPDATES_URL !== undefined) {
    return environment.DESKTOP_E2E_UPDATES_URL
  }
  if (environment.WINGIT_ENABLE_UPDATES !== '1') {
    return undefined
  }
  const value = environment.WINGIT_UPDATES_URL
  if (value === undefined || value.length === 0) {
    throw new Error(
      'WINGIT_UPDATES_URL is required when WINGIT_ENABLE_UPDATES=1'
    )
  }
  return requireHTTPSURL(value, 'WINGIT_UPDATES_URL')
}

export function getAutomaticUpdatesEnabled(
  environment: IReleaseEnvironment = process.env as IReleaseEnvironment
) {
  return (
    environment.WINGIT_ENABLE_AUTOMATIC_UPDATES === '1' &&
    getConfiguredUpdatesURL(environment) !== undefined
  )
}

export function getWinGitSigningMetadata(
  environment: IReleaseEnvironment = process.env as IReleaseEnvironment
): IWinGitSigningMetadata {
  const Endpoint = environment.WINGIT_AZURE_SIGNING_ENDPOINT
  const CodeSigningAccountName = environment.WINGIT_AZURE_SIGNING_ACCOUNT
  const CertificateProfileName = environment.WINGIT_AZURE_SIGNING_PROFILE

  if (
    Endpoint === undefined ||
    CodeSigningAccountName === undefined ||
    CertificateProfileName === undefined
  ) {
    throw new Error(
      'Publishable Windows builds require the WinGit Azure signing endpoint, account, and profile'
    )
  }

  return {
    Endpoint: requireHTTPSURL(Endpoint, 'WINGIT_AZURE_SIGNING_ENDPOINT'),
    CodeSigningAccountName,
    CertificateProfileName,
  }
}
