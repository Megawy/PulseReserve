import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type AlertTone = 'error' | 'success' | 'info' | 'warning';

/**
 * Inline message. Errors use role="alert" (announced immediately), other tones role="status" (polite). The tone is
 * also carried by an icon glyph and the optional title, never by colour alone.
 */
@Component({
  selector: 'app-alert',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': '"alert alert--" + tone()', '[attr.role]': 'role()' },
  template: `
    <span class="alert__icon" aria-hidden="true"
      ><span>{{ icon() }}</span></span
    >
    <div class="alert__body">
      @if (heading()) {
        <p class="alert__title">{{ heading() }}</p>
      }
      <ng-content />
    </div>
  `,
})
export class Alert {
  readonly tone = input<AlertTone>('info');
  readonly heading = input('');

  protected readonly role = computed(() => (this.tone() === 'error' ? 'alert' : 'status'));
  protected readonly icon = computed(
    () => ({ error: '!', warning: '!', success: '✓', info: 'i' })[this.tone()],
  );
}
