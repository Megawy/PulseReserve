import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { authGuard } from './auth.guard';
import { AuthService } from './auth.service';
import { isPlausibleEmail, passwordHint } from '../../features/auth/auth-form';

describe('authGuard', () => {
  function run(authenticated: boolean) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { isAuthenticated: signal(authenticated) } },
      ],
    });
    return TestBed.runInInjectionContext(() =>
      authGuard({} as never, { url: '/account/orders' } as RouterStateSnapshot),
    );
  }

  it('lets signed-in users through', () => {
    expect(run(true)).toBe(true);
  });

  it('sends signed-out users to sign in and remembers where they were going', () => {
    const result = run(false) as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(result)).toBe(
      '/login?returnUrl=%2Faccount%2Forders',
    );
  });
});

describe('auth form helpers', () => {
  it('accepts plausible emails only', () => {
    expect(isPlausibleEmail('a@example.com')).toBe(true);
    expect(isPlausibleEmail(' a@example.com ')).toBe(true);
    expect(isPlausibleEmail('nope')).toBe(false);
    expect(isPlausibleEmail('a@b')).toBe(false);
    expect(isPlausibleEmail('')).toBe(false);
  });

  it('guides password length and strength', () => {
    expect(passwordHint('')).toBeNull();
    expect(passwordHint('short')?.tone).toBe('danger');
    expect(passwordHint('shortpassword')?.tone).toBe('warning');
    expect(passwordHint('Str0ng-and-long-enough')?.tone).toBe('success');
  });
});
