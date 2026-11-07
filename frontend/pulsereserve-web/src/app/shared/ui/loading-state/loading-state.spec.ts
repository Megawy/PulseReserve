import { TestBed } from '@angular/core/testing';
import { LoadingState } from './loading-state';

describe('LoadingState', () => {
  it('announces the default message via a polite status region', async () => {
    const fixture = TestBed.createComponent(LoadingState);
    await fixture.whenStable();
    const status = (fixture.nativeElement as HTMLElement).querySelector('[role="status"]');
    expect(status?.getAttribute('aria-live')).toBe('polite');
    expect(status?.textContent).toContain('Loading');
  });

  it('shows a custom message', async () => {
    const fixture = TestBed.createComponent(LoadingState);
    fixture.componentRef.setInput('message', 'Loading venue…');
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Loading venue…');
  });
});
