import packageInfo from '../../package.json'

const {
  bundleID,
  bundleIDDevelopment,
  companyName,
  name,
  protocols,
  productName,
  version,
  windowsAppUserModelID,
  windowsAppUserModelIDDevelopment,
  windowsExecutableName,
} = packageInfo

function isDevelopment(environment: string | undefined) {
  return environment === 'development'
}

export function getProductName(environment = process.env.NODE_ENV) {
  return isDevelopment(environment) ? `${productName}-dev` : productName
}

export function getCompanyName() {
  return companyName
}

export function getPackageName() {
  return name
}

export function getVersion() {
  return version
}

export function getBundleID(environment = process.env.NODE_ENV) {
  return isDevelopment(environment) ? bundleIDDevelopment : bundleID
}

export function getWindowsAppUserModelID(environment = process.env.NODE_ENV) {
  return isDevelopment(environment)
    ? windowsAppUserModelIDDevelopment
    : windowsAppUserModelID
}

export function getWindowsExecutableName() {
  return windowsExecutableName
}

export function getProtocolSchemes(environment = process.env.NODE_ENV) {
  const authentication = isDevelopment(environment)
    ? protocols.authenticationDevelopment
    : protocols.authentication

  return [protocols.openRepository, protocols.oauth, authentication]
}
