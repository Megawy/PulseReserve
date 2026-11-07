import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { App } from './app';

describe('App shell', () => {
  async function create() {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            user: signal(null),
            isAdmin: signal(false),
            isAuthenticated: signal(false),
            logout: vi.fn(),
            load: vi.fn(),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('provides banner, navigation, main and contentinfo landmarks', async () => {
    const el = await create();
    expect(el.querySelector('header')).not.toBeNull();
    expect(el.querySelector('nav[aria-label="Primary"]')).not.toBeNull();
    expect(el.querySelector('main#main-content')).not.toBeNull();
    expect(el.querySelector('footer')).not.toBeNull();
  });

  it('puts the skip link first and moves focus to main without navigating', async () => {
    const el = await create();
    document.body.appendChild(el);
    const skip = el.querySelector('a.skip-link') as HTMLAnchorElement;
    expect(el.querySelector('a, button')).toBe(skip);
    const event = new MouseEvent('click', { bubbles: true, cancelable: true });
    skip.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(el.querySelector('main'));
    el.remove();
  });

  it('keeps the main element out of the tab order but programmatically focusable', async () => {
    const el = await create();
    expect(el.querySelector('main')?.getAttribute('tabindex')).toBe('-1');
  });
});
