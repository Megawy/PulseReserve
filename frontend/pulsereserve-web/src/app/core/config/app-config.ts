import { InjectionToken } from '@angular/core';

/**
 * Base path for API calls. Relative on purpose: the dev server proxies `/api` (proxy.conf.mjs).
 * UNVERIFIED ASSUMPTION: production deployment is same-origin (backend doc 10, D-03 is only a
 * proposal). Change this token's provider if the approved deployment differs.
 */
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => '/api',
});

/** Client-side request timeout. A frontend default, not a backend-specified value. */
export const API_REQUEST_TIMEOUT_MS = new InjectionToken<number>('API_REQUEST_TIMEOUT_MS', {
  providedIn: 'root',
  factory: () => 15_000,
});
