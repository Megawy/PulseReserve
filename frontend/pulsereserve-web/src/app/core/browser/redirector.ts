import { Injectable } from '@angular/core';

/**
 * The one place the app leaves itself for a provider-hosted payment page. The URL always comes from the server (the
 * attempt's `checkoutUrl`), is never built client-side, and is followed only when it is an absolute https URL.
 * Wrapped in a service so tests can observe the redirect without navigating.
 */
@Injectable({ providedIn: 'root' })
export class Redirector {
  /** Returns whether the browser was sent to the page. */
  toProvider(url: string | null | undefined): boolean {
    if (!url) {
      return false;
    }
    let target: URL;
    try {
      target = new URL(url);
    } catch {
      return false;
    }
    if (target.protocol !== 'https:') {
      return false;
    }
    globalThis.location.assign(target.toString());
    return true;
  }
}
