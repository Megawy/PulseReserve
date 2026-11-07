import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ToastService } from './toast.service';

/** Renders the toast list once, at the app root. Errors are announced assertively, the rest politely. */
@Component({
  selector: 'app-toast-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toast-host" aria-live="polite" aria-atomic="false">
      @for (t of toasts.toasts(); track t.id) {
        <div
          class="toast"
          [class]="'toast toast--' + t.tone"
          [attr.role]="t.tone === 'error' ? 'alert' : 'status'"
        >
          <span class="toast__text">{{ t.text }}</span>
          <button
            type="button"
            class="toast__close"
            aria-label="Dismiss notification"
            (click)="toasts.dismiss(t.id)"
          >
            ×
          </button>
        </div>
      }
    </div>
  `,
})
export class ToastHost {
  protected readonly toasts = inject(ToastService);
}
