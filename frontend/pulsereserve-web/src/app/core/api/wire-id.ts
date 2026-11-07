/**
 * Wire format of strongly-typed IDs in Catalog GET responses.
 *
 * The backend declares IDs as `readonly record struct EventId(Guid Value)`
 * (Catalog.Contracts/Ids/Ids.cs) with no custom JsonConverter, so System.Text.Json serialises them
 * as `{ "value": "<guid>" }` (nullable IDs as `null`). VERIFIED 2026-10-20 against live responses of
 * the running API and by the backend HTTP tests (CatalogApiHttpTests). Request IDs and
 * CreatedIdResponse use plain Guid strings (not handled here).
 *
 * This file is the ONLY place that knows the shape. If the backend adds a JsonConverter for IDs,
 * revisit this file and wire-id.spec.ts.
 */
export interface WireId {
  readonly value: string;
}

export class WireFormatError extends Error {
  constructor(field: string) {
    super(`Unexpected response format for "${field}".`);
    this.name = 'WireFormatError';
  }
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Converts a provisional wire ID into a plain GUID string, or throws WireFormatError. */
export function unwrapWireId(raw: unknown, field: string): string {
  if (typeof raw === 'object' && raw !== null && !Array.isArray(raw)) {
    const value = (raw as Record<string, unknown>)['value'];
    if (typeof value === 'string' && GUID.test(value)) {
      return value;
    }
  }
  throw new WireFormatError(field);
}

/** Like unwrapWireId, but `null`/`undefined` map to null. */
export function unwrapOptionalWireId(raw: unknown, field: string): string | null {
  return raw === null || raw === undefined ? null : unwrapWireId(raw, field);
}
