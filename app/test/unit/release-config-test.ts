import { describe, it } from 'node:test'
import assert from 'node:assert'
import {
  getAutomaticUpdatesEnabled,
  getConfiguredUpdatesURL,
  getWinGitSigningMetadata,
} from '../../../script/release-config'

describe('WinGit release configuration', () => {
  it('ships with update checks disabled by default', () => {
    assert.equal(getConfiguredUpdatesURL({}), undefined)
    assert.equal(getAutomaticUpdatesEnabled({}), false)
  })

  it('allows an owned HTTPS endpoint without enabling background checks', () => {
    const environment = {
      WINGIT_ENABLE_UPDATES: '1',
      WINGIT_UPDATES_URL: 'https://updates.example.test/wingit/x64/latest',
    }

    assert.equal(
      getConfiguredUpdatesURL(environment),
      'https://updates.example.test/wingit/x64/latest'
    )
    assert.equal(getAutomaticUpdatesEnabled(environment), false)
  })

  it('enables background checks only through the separate release gate', () => {
    assert.equal(
      getAutomaticUpdatesEnabled({
        WINGIT_ENABLE_UPDATES: '1',
        WINGIT_ENABLE_AUTOMATIC_UPDATES: '1',
        WINGIT_UPDATES_URL: 'https://updates.example.test/wingit/x64/latest',
      }),
      true
    )
  })

  it('rejects an insecure production update endpoint', () => {
    assert.throws(() =>
      getConfiguredUpdatesURL({
        WINGIT_ENABLE_UPDATES: '1',
        WINGIT_UPDATES_URL: 'http://updates.example.test/latest',
      })
    )
  })

  it('requires a complete WinGit signing identity', () => {
    assert.throws(() => getWinGitSigningMetadata({}))
    assert.deepEqual(
      getWinGitSigningMetadata({
        WINGIT_AZURE_SIGNING_ENDPOINT: 'https://example.codesigning.azure.net/',
        WINGIT_AZURE_SIGNING_ACCOUNT: 'WinGitSigning',
        WINGIT_AZURE_SIGNING_PROFILE: 'WinGitRelease',
      }),
      {
        Endpoint: 'https://example.codesigning.azure.net/',
        CodeSigningAccountName: 'WinGitSigning',
        CertificateProfileName: 'WinGitRelease',
      }
    )
  })
})
