import { LicenseLookup } from 'legal-eagle'
import { existsSync, readFileSync } from 'fs'
import { join } from 'path'
import frontMatter from 'front-matter'

interface IPackageJSON {
  readonly version: string
  readonly optionalDependencies?: Readonly<Record<string, string>>
}

const appNodeModulesPath = join(__dirname, '..', '..', 'app', 'node_modules')

const getPackageJSONPath = (packageName: string) =>
  join(appNodeModulesPath, ...packageName.split('/'), 'package.json')

const readPackageJSON = (packageName: string): IPackageJSON =>
  JSON.parse(readFileSync(getPackageJSONPath(packageName), 'utf8'))

const readOptionalPackageJSON = (
  packageName: string
): IPackageJSON | undefined => {
  const packageJSONPath = getPackageJSONPath(packageName)

  return existsSync(packageJSONPath)
    ? JSON.parse(readFileSync(packageJSONPath, 'utf8'))
    : undefined
}

const codexLicenseEntries: LicenseLookup = {}

const codexPackage = readPackageJSON('@openai/codex')
const apacheLicensePath = join(
  __dirname,
  '..',
  '..',
  'app',
  'static',
  'common',
  'choosealicense.com',
  '_licenses',
  'apache-2.0.txt'
)
const apacheLicense = frontMatter(
  readFileSync(apacheLicensePath, 'utf8')
).body.trim()
const codexLicenseEntry = {
  license: 'Apache-2.0',
  source: 'https://github.com/openai/codex/blob/rust-v0.151.0/LICENSE',
  sourceText: `${apacheLicense}\n`,
  repository: 'git+https://github.com/openai/codex',
}

codexLicenseEntries[`@openai/codex@${codexPackage.version}`] = codexLicenseEntry

for (const packageName of Object.keys(
  codexPackage.optionalDependencies ?? {}
)) {
  if (!packageName.startsWith('@openai/codex-')) {
    continue
  }

  const installedPackage = readOptionalPackageJSON(packageName)
  if (installedPackage !== undefined) {
    codexLicenseEntries[`@openai/codex@${installedPackage.version}`] =
      codexLicenseEntry
  }
}

export const licenseOverrides: LicenseLookup = {
  ...codexLicenseEntries,
}
