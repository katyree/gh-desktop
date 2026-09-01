const ConsentLifetimeMs = 30 * 24 * 60 * 60 * 1000

export const CodexPrivacyConsentStorageKey =
  'codex-commit-message-privacy-consent-v1'

type ConsentTimestamps = Readonly<Record<string, number>>

function parseConsentTimestamps(raw: string | null): ConsentTimestamps {
  if (raw === null) {
    return {}
  }
  try {
    const parsed: unknown = JSON.parse(raw)
    if (
      typeof parsed !== 'object' ||
      parsed === null ||
      Array.isArray(parsed)
    ) {
      return {}
    }
    return Object.fromEntries(
      Object.entries(parsed)
        .filter(
          ([repositoryId, timestamp]) =>
            /^\d+$/.test(repositoryId) &&
            typeof timestamp === 'number' &&
            Number.isFinite(timestamp) &&
            timestamp > 0
        )
        .slice(0, 10_000)
    )
  } catch {
    return {}
  }
}

/** Persists path-free, per-repository consent for Codex data transfer. */
export class CodexPrivacyConsentStore {
  private consentTimestamps: ConsentTimestamps

  public constructor(
    private readonly storage: Pick<
      Storage,
      'getItem' | 'setItem' | 'removeItem'
    >,
    private readonly now: () => number = Date.now
  ) {
    this.consentTimestamps = parseConsentTimestamps(
      storage.getItem(CodexPrivacyConsentStorageKey)
    )
  }

  public hasFreshConsent(repositoryId: number): boolean {
    const timestamp = this.consentTimestamps[String(repositoryId)]
    return (
      timestamp !== undefined && this.now() - timestamp <= ConsentLifetimeMs
    )
  }

  public acknowledge(repositoryId: number): void {
    this.consentTimestamps = {
      ...this.consentTimestamps,
      [String(repositoryId)]: this.now(),
    }
    this.storage.setItem(
      CodexPrivacyConsentStorageKey,
      JSON.stringify(this.consentTimestamps)
    )
  }

  public resetAll(): void {
    this.consentTimestamps = {}
    this.storage.removeItem(CodexPrivacyConsentStorageKey)
  }
}
