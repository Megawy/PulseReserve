import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { LiveStatus } from '../../../core/live/live.models';
import { LiveService } from '../../../core/live/live.service';
import { LiveIndicator } from './live-indicator';

describe('LiveIndicator', () => {
  const status = signal<LiveStatus>('off');
  const degraded = signal(false);

  function render(state: LiveStatus, down = false) {
    status.set(state);
    degraded.set(down);
    TestBed.configureTestingModule({
      providers: [{ provide: LiveService, useValue: { status, degraded } }],
    });
    const fixture = TestBed.createComponent(LiveIndicator);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      badge: el.querySelector<HTMLElement>('.badge'),
      announcement: el.querySelector('[role="status"]')?.textContent?.trim() ?? '',
    };
  }

  it('says nothing before the connection exists or while it is still connecting', () => {
    for (const state of ['off', 'connecting'] as const) {
      TestBed.resetTestingModule();
      const { badge, announcement } = render(state);
      expect(badge).toBeNull();
      expect(announcement).toBe('');
    }
  });

  it('says nothing about a gap too short to matter', () => {
    const { badge } = render('reconnecting', false);

    expect(badge).toBeNull();
  });

  it('says "Live" only when the server has confirmed the connection, without announcing it aloud', () => {
    const { badge, announcement } = render('live');

    expect(badge?.textContent?.trim()).toBe('Live');
    expect(badge?.classList.contains('badge--success')).toBe(true);
    expect(announcement).toBe('');
  });

  it('says that updates are paused once the connection has been down for a while, and announces it', () => {
    const { badge, announcement } = render('reconnecting', true);

    expect(badge?.textContent?.trim()).toBe('Reconnecting');
    expect(badge?.classList.contains('badge--warning')).toBe(true);
    expect(announcement).toContain('Live updates are paused');
  });

  it('says that changes appear on refresh when the server offers no live updates', () => {
    const { badge, announcement } = render('unavailable', true);

    expect(badge?.textContent?.trim()).toBe('Live updates off');
    expect(announcement).toContain('when you refresh or act');
  });

  it('follows the connection as it changes', () => {
    const { fixture } = render('live');
    status.set('reconnecting');
    degraded.set(true);
    fixture.detectChanges();

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('.badge')?.textContent?.trim(),
    ).toBe('Reconnecting');

    status.set('live');
    degraded.set(false);
    fixture.detectChanges();

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('.badge')?.textContent?.trim(),
    ).toBe('Live');
  });
});
