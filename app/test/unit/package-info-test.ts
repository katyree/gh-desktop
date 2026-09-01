import { describe, it } from 'node:test'
import assert from 'node:assert'
import {
  getBundleID,
  getCompanyName,
  getProductName,
  getProtocolSchemes,
  getWindowsAppUserModelID,
  getWindowsExecutableName,
} from '../../package-info'

describe('package identity', () => {
  it('uses the WinGit production identity', () => {
    assert.equal(getProductName('production'), 'WinGit')
    assert.equal(getBundleID('production'), 'com.katyree.WinGit')
    assert.equal(
      getWindowsAppUserModelID('production'),
      'com.squirrel.WinGit.WinGit'
    )
    assert.equal(getWindowsExecutableName(), 'WinGit')
    assert.equal(getCompanyName(), 'WinGit Contributors')
  })

  it('uses a distinct WinGit development identity', () => {
    assert.equal(getProductName('development'), 'WinGit-dev')
    assert.equal(getBundleID('development'), 'com.katyree.WinGit.Dev')
    assert.equal(
      getWindowsAppUserModelID('development'),
      'com.squirrel.WinGitDev.WinGitDev'
    )

    assert.notEqual(getProductName('development'), getProductName('production'))
    assert.notEqual(getBundleID('development'), getBundleID('production'))
    assert.notEqual(
      getWindowsAppUserModelID('development'),
      getWindowsAppUserModelID('production')
    )
  })

  it('uses only WinGit-owned protocol schemes', () => {
    assert.deepEqual(getProtocolSchemes('production'), [
      'wingit',
      'x-wingit-client',
      'x-wingit-auth',
    ])
    assert.deepEqual(getProtocolSchemes('development'), [
      'wingit',
      'x-wingit-client',
      'x-wingit-dev-auth',
    ])

    for (const protocol of [
      ...getProtocolSchemes('production'),
      ...getProtocolSchemes('development'),
    ]) {
      assert(!protocol.startsWith('x-github-'))
      assert.notEqual(protocol, 'github-windows')
      assert.notEqual(protocol, 'github-mac')
    }
  })
})
