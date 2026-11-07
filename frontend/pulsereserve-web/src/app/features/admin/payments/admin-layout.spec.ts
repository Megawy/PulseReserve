import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { ApiError } from '../../../core/api/api-error';
import { AuthService } from '../../../core/auth/auth.service';
import { AdminLayout } from './admin-layout';
import { Overview } from './overview';

describe('AdminLayout', () => {
  function mount(expired: boolean) {
    const sessionExpired = signal(expired);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { sessionExpired, user: signal(null) } },
      ],
    });
    const fixture = TestBed.createComponent(AdminLayout);
    return { fixture, sessionExpired, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the section navigation while the session is valid', async () => {
    const { fixture, el } = mount(false);
    await fixture.whenStable();
    expect(el.querySelector('nav.subnav')).not.toBeNull();
    expect(el.textContent).not.toContain('Your session has ended');
  });

  it('replaces the console with a sign-in prompt once the session has ended, so no stale data stays on screen', async () => {
    const { fixture, sessionExpired, el } = mount(false);
    await fixture.whenStable();
    sessionExpired.set(true);
    await fixture.whenStable();
    expect(el.textContent).toContain('Your session has ended');
    expect(el.querySelector('nav.subnav')).toBeNull();
    expect(el.querySelector('router-outlet')).toBeNull();
  });
});

describe('Overview process-now session handling', () => {
  it('explains an ended session instead of showing a raw error code', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AdminPaymentsApiClient,
          useValue: {
            overview: () => of({ tasksByState: {}, refundsByState: {}, outboxByState: {} }),
            processNow: () =>
              throwError(
                () => new ApiError('problem', 401, 'Auth.Unauthorized', null, 'Auth.Unauthorized'),
              ),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(Overview);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Process now'))?.click();
    await fixture.whenStable();
    expect(el.textContent).toContain('Your session has ended. Sign in again to continue.');
    expect(el.textContent).not.toContain('Auth.Unauthorized');
  });
});
