import { OrderedWebRequest } from './ordered-webrequest'

export const ForbiddenProductServiceHosts = new Set([
  'browser-intake-datadoghq.com',
  'central.github.com',
  'desktop.github.com',
  'desktop.githubusercontent.com',
])

function getHTTPHost(requestURL: string): string | null {
  try {
    const url = new URL(requestURL)
    return url.protocol === 'http:' || url.protocol === 'https:'
      ? url.hostname.toLowerCase()
      : null
  } catch {
    return null
  }
}

export function isForbiddenProductServiceURL(requestURL: string) {
  const host = getHTTPHost(requestURL)
  return host !== null && ForbiddenProductServiceHosts.has(host)
}

export function installProductServiceIsolation(
  orderedWebRequest: OrderedWebRequest
) {
  const logNetworkHosts = process.env.WINGIT_LOG_NETWORK_HOSTS === '1'
  const loggedHosts = new Set<string>()

  orderedWebRequest.onBeforeRequest.addEventListener(async details => {
    const host = getHTTPHost(details.url)
    if (host === null) {
      return {}
    }

    if (ForbiddenProductServiceHosts.has(host)) {
      log.error(`[ProductServiceIsolation] Blocked request to ${host}`)
      return { cancel: true }
    }

    if (logNetworkHosts && !loggedHosts.has(host)) {
      loggedHosts.add(host)
      log.info(`[NetworkHost] ${host}`)
    }

    return {}
  })
}
