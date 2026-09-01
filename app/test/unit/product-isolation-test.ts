import { describe, it } from 'node:test'
import assert from 'node:assert'
import { getUpdatesURL } from '../../../script/dist-info'
import { StatsReportingEnabled } from '../../src/lib/stats/stats-store'
import {
  WinGitChangelogURL,
  WinGitDocumentationURL,
  WinGitIssueURL,
  WinGitPackageIconURL,
  WinGitReleaseNotesURL,
  WinGitRepositoryURL,
} from '../../src/lib/product-links'
import {
  ForbiddenProductServiceHosts,
  isForbiddenProductServiceURL,
} from '../../src/main-process/product-service-isolation'

describe('product service isolation', () => {
  it('ships without an update endpoint', () => {
    assert.equal(getUpdatesURL(), undefined)
  })

  it('ships with usage reporting disabled', () => {
    assert.equal(StatsReportingEnabled, false)
  })

  it('uses no GitHub Desktop service host for product links', () => {
    const productURLs = [
      WinGitChangelogURL,
      WinGitDocumentationURL,
      WinGitIssueURL,
      WinGitPackageIconURL,
      WinGitReleaseNotesURL,
      WinGitRepositoryURL,
    ]

    for (const productURL of productURLs) {
      assert(!ForbiddenProductServiceHosts.has(new URL(productURL).hostname))
    }
  })

  it('blocks inherited product service and telemetry URLs', () => {
    assert(isForbiddenProductServiceURL('https://central.github.com/a'))
    assert(isForbiddenProductServiceURL('https://desktop.github.com/'))
    assert(
      isForbiddenProductServiceURL(
        'https://desktop.githubusercontent.com/app-icon.ico'
      )
    )
    assert(
      isForbiddenProductServiceURL(
        'https://browser-intake-datadoghq.com/api/v2/rum'
      )
    )
    assert(!isForbiddenProductServiceURL(WinGitRepositoryURL))
    assert(!isForbiddenProductServiceURL('file:///C:/WinGit/index.html'))
    assert(!isForbiddenProductServiceURL('not a URL'))
  })
})
