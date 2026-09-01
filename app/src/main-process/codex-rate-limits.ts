import type {
  ICodexRateLimitState,
  ICodexRateLimitWindow,
} from '../lib/codex-ipc'

const NearLimitPercent = 80

export const unavailableCodexRateLimits: ICodexRateLimitState = {
  status: 'unavailable',
  primary: null,
  secondary: null,
  resetsAt: null,
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function parseResetTime(value: unknown): string | null | undefined {
  if (value === null) {
    return null
  }
  if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) {
    return undefined
  }

  const date = new Date(value * 1000)
  return Number.isNaN(date.getTime()) ? undefined : date.toISOString()
}

function parseWindow(value: unknown): ICodexRateLimitWindow | null | undefined {
  if (value === null) {
    return null
  }
  if (!isRecord(value)) {
    return undefined
  }

  const usedPercent = value.usedPercent
  const resetsAt = parseResetTime(value.resetsAt)
  if (
    typeof usedPercent !== 'number' ||
    !Number.isFinite(usedPercent) ||
    usedPercent < 0 ||
    resetsAt === undefined
  ) {
    return undefined
  }

  return { usedPercent, resetsAt }
}

/** Convert the versioned App Server payload into a stable display contract. */
export function sanitizeCodexRateLimits(
  response: unknown
): ICodexRateLimitState {
  if (!isRecord(response) || !isRecord(response.rateLimits)) {
    return unavailableCodexRateLimits
  }

  const snapshot = response.rateLimits
  const primary = parseWindow(snapshot.primary)
  const secondary = parseWindow(snapshot.secondary)
  if (primary === undefined || secondary === undefined) {
    return unavailableCodexRateLimits
  }

  const windows = [primary, secondary].filter(
    (window): window is ICodexRateLimitWindow => window !== null
  )
  if (windows.length === 0) {
    return unavailableCodexRateLimits
  }

  const explicitlyExhausted =
    snapshot.spendControlReached === true ||
    (typeof snapshot.rateLimitReachedType === 'string' &&
      snapshot.rateLimitReachedType.length > 0)
  const highestUsedPercent = Math.max(
    ...windows.map(window => window.usedPercent)
  )
  const status =
    explicitlyExhausted || highestUsedPercent >= 100
      ? 'exhausted'
      : highestUsedPercent >= NearLimitPercent
      ? 'near-limit'
      : 'available'
  const resetTimes = windows
    .map(window => window.resetsAt)
    .filter((value): value is string => value !== null)
    .sort()

  return {
    status,
    primary,
    secondary,
    resetsAt: resetTimes.at(0) ?? null,
  }
}
