import { Component, ElementRef, effect, input, output, viewChild } from '@angular/core';

/**
 * Modal confirmation built on the native <dialog> element (focus trap, Escape to close and inert background come from
 * the platform). Focus starts on the safe "cancel" action; the destructive action is visually distinct. Projected
 * content shows the exact subject and consequences (e.g. an order and an amount). While `busy`, both actions are
 * disabled and Escape is ignored so a request in flight cannot be abandoned or repeated.
 */
@Component({
  selector: 'app-confirm-dialog',
  template: `
    <dialog
      #dialog
      class="dialog"
      [attr.aria-labelledby]="headingId"
      [attr.aria-describedby]="messageId"
      (cancel)="onCancel($event)"
    >
      <h2 [id]="headingId">{{ heading() }}</h2>
      <p class="muted" [id]="messageId">{{ message() }}</p>
      <ng-content />
      <div class="dialog__actions">
        <button
          #cancelButton
          type="button"
          class="btn btn--secondary"
          [disabled]="busy()"
          (click)="cancelled.emit()"
        >
          {{ cancelLabel() }}
        </button>
        <button
          type="button"
          class="btn"
          [class.btn--danger]="tone() === 'danger'"
          [class.btn--primary]="tone() === 'primary'"
          [disabled]="busy() || confirmDisabled()"
          [attr.aria-busy]="busy()"
          (click)="confirmed.emit()"
        >
          {{ busy() ? busyLabel() : confirmLabel() }}
        </button>
      </div>
    </dialog>
  `,
  styles: `
    h2 {
      margin-bottom: var(--space-2);
    }
  `,
})
export class ConfirmDialog {
  private static nextId = 0;

  /** Unique per instance: several dialogs live on one page, and a shared id would name each by the first one's heading. */
  private readonly uid = ConfirmDialog.nextId++;
  protected readonly headingId = `confirm-heading-${this.uid}`;
  protected readonly messageId = `confirm-message-${this.uid}`;

  readonly open = input.required<boolean>();
  readonly heading = input.required<string>();
  readonly message = input.required<string>();
  readonly confirmLabel = input('Confirm');
  readonly cancelLabel = input('Cancel');
  readonly busyLabel = input('Working…');
  readonly busy = input(false);
  /** Keeps the confirming action unavailable until the caller's own precondition (e.g. a required checkbox) is met. */
  readonly confirmDisabled = input(false);
  readonly tone = input<'danger' | 'primary'>('danger');
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();

  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly cancelButton = viewChild.required<ElementRef<HTMLButtonElement>>('cancelButton');

  constructor() {
    effect(() => {
      const el = this.dialog().nativeElement;
      if (this.open()) {
        if (!el.open) {
          if (typeof el.showModal === 'function') {
            el.showModal();
          } else {
            el.setAttribute('open', '');
          }
        }
        this.cancelButton().nativeElement.focus();
      } else if (el.open) {
        if (typeof el.close === 'function') {
          el.close();
        } else {
          el.removeAttribute('open');
        }
      }
    });
  }

  protected onCancel(event: Event): void {
    // Escape key: let the parent own the open state, and never abandon a request in flight.
    event.preventDefault();
    if (!this.busy()) {
      this.cancelled.emit();
    }
  }
}
