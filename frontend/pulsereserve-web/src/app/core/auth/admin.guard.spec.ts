import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { adminGuard } from './admin.guard';
import { AuthService } from './auth.service';

describe('adminGuard', () => {
  function run(authenticated: boolean, admin: boolean, url = '/admin/payments/refunds') {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: { isAuthenticated: signal(authenticated), isAdmin: signal(admin) },
        },
      ],
    });
    return TestBed.runInInjectionContext(() =>
      adminGuard({} as never, { url } as RouterStateSnapshot),
    );
  }

  it('lets an administrator through', () => {
    expect(run(true, true)).toBe(true);
  });

  it('sends an anonymous visitor to sign in and remembers the console page they wanted', () => {
    const result = run(false, false) as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(result)).toBe(
      '/login?returnUrl=%2Fadmin%2Fpayments%2Frefunds',
    );
  });

  it('shows a signed-in non-administrator the access-denied page instead of an admin shell', () => {
    const result = run(true, false) as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/access-denied');
  });

  it('never treats an anonymous visitor as an administrator, whatever the admin hint says', () => {
    const result = run(false, true);
    expect(result).not.toBe(true);
  });
});
