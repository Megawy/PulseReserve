/**
 * Shared formatting conventions. Every screen formats money, dates and times through these helpers so the same value
 * always reads the same way. Dates arrive as ISO-8601 UTC strings and are shown in the viewer's locale and time zone.
 */

/** Formats a money amount; falls back to "amount CODE" if the currency code is not recognised. */
export function formatMoney(amount: number, currency: string): string {
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(amount);
  } catch {
    return `${amount.toFixed(2)} ${currency}`;
  }
}

/**
 * Formats an amount given in a currency's MINOR units (cents), as providers report it. The number of decimals comes from the
 * platform's currency data (0 for JPY, 3 for KWD, 2 otherwise), never from a table in this app. An unreadable currency is shown
 * honestly as raw minor units.
 */
export function formatMinorUnits(
  minor: number | null | undefined,
  currency: string | null | undefined,
): string {
  if (minor === null || minor === undefined) {
    return 'not stated';
  }
  if (!currency) {
    return `${minor} (minor units, currency not stated)`;
  }
  try {
    const digits =
      new Intl.NumberFormat(undefined, { style: 'currency', currency }).resolvedOptions()
        .maximumFractionDigits ?? 2;
    return formatMoney(minor / 10 ** digits, currency);
  } catch {
    return `${minor} ${currency} (minor units)`;
  }
}

/**
 * A waiting time in words an operator can act on: "under a minute", "12 min", "3 h 5 min", "2 d 4 h". Whole seconds in, never a clock
 * time, so it does not depend on the browser's timezone or clock; a missing or negative value reads as "unknown".
 */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0) {
    return 'unknown';
  }
  const total = Math.floor(seconds);
  if (total < 60) {
    return 'under a minute';
  }
  const minutes = Math.floor(total / 60);
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return minutes % 60 === 0 ? `${hours} h` : `${hours} h ${minutes % 60} min`;
  }
  const days = Math.floor(hours / 24);
  return hours % 24 === 0 ? `${days} d` : `${days} d ${hours % 24} h`;
}

export function isValidDate(iso: string | null | undefined): boolean {
  return !!iso && Number.isFinite(Date.parse(iso));
}

function fmt(iso: string | null | undefined, options: Intl.DateTimeFormatOptions): string {
  return isValidDate(iso) ? new Intl.DateTimeFormat(undefined, options).format(new Date(iso!)) : '';
}

/** "Fri, Mar 1, 2030" */
export function formatDay(iso: string | null | undefined): string {
  return fmt(iso, { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' });
}

/** "10:00 PM" */
export function formatTime(iso: string | null | undefined): string {
  return fmt(iso, { hour: 'numeric', minute: '2-digit' });
}

/** "Fri, Mar 1, 2030, 10:00 PM" */
export function formatDateTime(iso: string | null | undefined): string {
  return fmt(iso, {
    weekday: 'short',
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  });
}

/** "Fri, Mar 1, 2030 · 10:00 PM – 1:00 AM" (end omitted when it is not a valid date). */
export function formatDateRange(startIso: string, endIso: string | null | undefined): string {
  const start = formatDateTime(startIso);
  return isValidDate(endIso) ? `${start} – ${formatTime(endIso)}` : start;
}

/** Month abbreviation and day number for the calendar tile on event cards. */
export function dateTile(iso: string | null | undefined): { month: string; day: string } | null {
  return isValidDate(iso)
    ? { month: fmt(iso, { month: 'short' }), day: fmt(iso, { day: 'numeric' }) }
    : null;
}

/** First eight characters of an identifier, for dense tables (the full value stays in the title and detail views). */
export function shortId(id: string | null | undefined): string {
  return id ? id.slice(0, 8) : '';
}
