export type CountdownState =
  | { readonly kind: 'invalid' }
  | { readonly kind: 'running'; readonly remainingSeconds: number }
  | { readonly kind: 'expired' };

/**
 * Pure countdown computation from a server-provided ISO expiry timestamp and the (server-adjusted)
 * current time. An unparsable timestamp is `invalid`, never silently treated as running.
 */
export function computeCountdown(expiresAtUtc: string, nowMs: number): CountdownState {
  const expiresMs = Date.parse(expiresAtUtc);
  if (!Number.isFinite(expiresMs)) {
    return { kind: 'invalid' };
  }
  const remainingMs = expiresMs - nowMs;
  if (remainingMs <= 0) {
    return { kind: 'expired' };
  }
  return { kind: 'running', remainingSeconds: Math.ceil(remainingMs / 1000) };
}

export function formatRemaining(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const minutes = Math.floor(s / 60);
  const seconds = s % 60;
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}
