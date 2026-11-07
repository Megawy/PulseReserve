import { TestBed } from '@angular/core/testing';
import { ErrorState } from './error-state';

describe('ErrorState', () => {
  async function create(retryable: boolean) {
    const fixture = TestBed.createComponent(ErrorState);
    fixture.componentRef.setInput('message', 'Could not reach the server.');
    fixture.componentRef.setInput('retryable', retryable);
    await fixture.whenStable();
    return fixture;
  }

  it('renders the message in an alert region', async () => {
    const fixture = await create(false);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role="alert"]')?.textContent).toContain(
      'Could not reach the server.',
    );
  });

  it('hides the retry button when not retryable', async () => {
    const fixture = await create(false);
    expect((fixture.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });

  it('emits retry exactly once per click when retryable', async () => {
    const fixture = await create(true);
    let count = 0;
    fixture.componentInstance.retry.subscribe(() => count++);
    const button = (fixture.nativeElement as HTMLElement).querySelector('button');
    expect(button?.textContent).toContain('Try again');
    button?.click();
    expect(count).toBe(1);
  });
});
