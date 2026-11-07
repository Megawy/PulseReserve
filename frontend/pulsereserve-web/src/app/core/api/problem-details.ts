/**
 * The only ProblemDetails fields treated as supported: `status`, `title`, `detail`.
 * Source: ErrorMapping.ToProblemResult (Results.Problem(detail, title, statusCode)).
 * The one extension member read is `unavailableSeatIds` (HoldSeats 409). `type`, `instance` and
 * `traceId` are NOT relied upon.
 * Backend doc 10 (M-03) notes there is no global ProblemDetails pipeline yet, so unhandled
 * errors (e.g. HTTP 500) may return a non-JSON body.
 */
export interface ProblemDetailsFields {
  readonly status?: number;
  readonly title?: string;
  readonly detail?: string;
  /** Extension member sent with Reservation.SeatsUnavailable (ReservationEndpoints.HoldSeats). */
  readonly unavailableSeatIds?: readonly string[];
}

/** Extracts the supported fields from an unknown error body. Returns null if none are usable. */
export function readProblemDetails(body: unknown): ProblemDetailsFields | null {
  if (typeof body !== 'object' || body === null || Array.isArray(body)) {
    return null;
  }
  const record = body as Record<string, unknown>;
  const status = typeof record['status'] === 'number' ? record['status'] : undefined;
  const title = typeof record['title'] === 'string' ? record['title'] : undefined;
  const detail = typeof record['detail'] === 'string' ? record['detail'] : undefined;
  if (status === undefined && title === undefined && detail === undefined) {
    return null;
  }
  const rawIds = record['unavailableSeatIds'];
  const unavailableSeatIds = Array.isArray(rawIds)
    ? rawIds.filter((id): id is string => typeof id === 'string')
    : undefined;
  return { status, title, detail, unavailableSeatIds };
}
