import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { LiveService } from '../../../core/live/live.service';
import { Tone } from '../../presentation';

interface IndicatorView {
  readonly label: string;
  readonly tone: Tone;
  readonly hint: string;
  readonly state: 'live' | 'reconnecting' | 'unavailable';
}

/**
 * Tells the truth about live updates, and says nothing when there is nothing to say. While the connection is up it shows "Live";
 * while it has been down for a moment it says the page is catching up; when the server does not offer live updates it says changes
 * appear on refresh. It never claims live updates before the server has confirmed the connection. Static (no motion), and the
 * announcement for assistive technology is spoken only when updates stop, never for every routine reconnect.
 */
@Component({
  selector: 'app-live-indicator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (view(); as v) {
      <span class="badge" [class]="'badge badge--' + v.tone" [attr.data-live]="v.state">{{
        v.label
      }}</span>
    }
    <span class="visually-hidden" role="status">{{ announcement() }}</span>
  `,
})
export class LiveIndicator {
  private readonly live = inject(LiveService);

  protected readonly view = computed<IndicatorView | null>(() => {
    if (this.live.status() === 'live') {
      return { label: 'Live', tone: 'success', hint: 'Live updates are on.', state: 'live' };
    }
    if (!this.live.degraded()) {
      return null; // not started, still connecting, or a gap too short to matter
    }
    return this.live.status() === 'unavailable'
      ? {
          label: 'Live updates off',
          tone: 'neutral',
          hint: 'Live updates are not available. Changes appear when you refresh or act.',
          state: 'unavailable',
        }
      : {
          label: 'Reconnecting',
          tone: 'warning',
          hint: 'Live updates are paused. This page catches up as soon as the connection returns.',
          state: 'reconnecting',
        };
  });

  protected readonly announcement = computed(() => {
    const view = this.view();
    return view && view.state !== 'live' ? view.hint : '';
  });
}
