import { Component, Injector, effect, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';
import { Alert } from '../../../shared/ui/alert/alert';
import { LiveIndicator } from '../../../shared/ui/live-indicator/live-indicator';
import { restoreFocus } from './ops-flow';

/**
 * Frame for the payment operations console: heading, section navigation and the session-ended notice. Reaching this
 * component already required the admin guard, but that is a courtesy; the API authorises every call itself.
 */
@Component({
  selector: 'app-admin-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Alert, LiveIndicator],
  template: `
    <div class="container page">
      <header class="page-header">
        <div>
          <p class="page-header__eyebrow">Operations</p>
          <h1>Payment operations</h1>
          <p class="page-header__lead">
            Reconciliation, refunds and background delivery. Every action here is recorded in the
            audit log with your administrator id.
          </p>
          <app-live-indicator />
        </div>
      </header>

      @if (auth.sessionExpired()) {
        <app-alert id="session-ended" tabindex="-1" tone="warning" heading="Your session has ended">
          Sign in again to keep working.
          <a [routerLink]="['/login']" [queryParams]="{ returnUrl: router.url }">Sign in</a>
        </app-alert>
      }

      @if (!auth.sessionExpired()) {
        <nav class="subnav" aria-label="Payment operations sections">
          <a
            routerLink="/admin/payments"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            [routerLinkActiveOptions]="{ exact: true }"
            >Overview</a
          >
          <a
            routerLink="/admin/payments/reconciliation"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            >Reconciliation</a
          >
          <a
            routerLink="/admin/payments/refunds"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            >Refunds</a
          >
          <a
            routerLink="/admin/payments/exceptions"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            >Exceptions</a
          >
          <a
            routerLink="/admin/payments/anomalies"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            >Anomalies</a
          >
          <a
            routerLink="/admin/payments/history"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            >History</a
          >
          <a
            routerLink="/admin/payments/outbox"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            >Outbox</a
          >
          <a
            routerLink="/admin/payments/audit"
            routerLinkActive="active"
            ariaCurrentWhenActive="page"
            >Audit log</a
          >
        </nav>

        <router-outlet />
      }
    </div>
  `,
  styles: `
    .subnav {
      display: flex;
      gap: var(--space-1);
      margin: var(--space-3) calc(-1 * var(--space-2)) var(--space-5);
      padding: var(
        --space-2
      ); /* room inside the scroll container so the focus ring is not clipped */
      overflow-x: auto;
      border-bottom: 1px solid var(--border);
    }
    .subnav a {
      flex: none;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-md);
      color: var(--foreground-muted);
      font-weight: var(--weight-semibold);
      text-decoration: none;
    }
    .subnav a:hover {
      background: var(--surface-hover);
      color: var(--foreground);
    }
    .subnav a.active {
      background: var(--primary-soft);
      color: var(--primary);
    }
  `,
})
export class AdminLayout {
  protected readonly auth = inject(AuthService);
  protected readonly router = inject(Router);
  private readonly injector = inject(Injector);

  constructor() {
    // The console is replaced by the sign-in prompt, so whatever had focus is gone: land on the prompt.
    effect(() => {
      if (this.auth.sessionExpired()) {
        restoreFocus(this.injector, 'session-ended');
      }
    });
  }
}
