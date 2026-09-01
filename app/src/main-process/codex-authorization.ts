const codexAuthorizationHosts = new Set([
  'auth.openai.com',
  'chatgpt.com',
  'www.chatgpt.com',
])

/** Accept only official HTTPS login pages before handing a URL to the OS. */
export function validateCodexAuthorizationURL(value: string) {
  let authorizationURL: URL
  try {
    authorizationURL = new URL(value)
  } catch {
    throw new Error('Codex returned an invalid authorization URL')
  }
  if (
    authorizationURL.protocol !== 'https:' ||
    authorizationURL.username.length > 0 ||
    authorizationURL.password.length > 0 ||
    !codexAuthorizationHosts.has(authorizationURL.hostname.toLowerCase())
  ) {
    throw new Error('Codex returned an untrusted authorization URL')
  }
  return authorizationURL.toString()
}
