import { describe, it } from 'node:test'
import assert from 'node:assert'
import {
  getBundleID,
  getCompanyName,
  getProductName,
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
})
