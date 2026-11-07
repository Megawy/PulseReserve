import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiError } from '../../core/api/api-error';
import { AuthService } from '../../core/auth/auth.service';
import { Login } from './login';
import { Register } from './register';
import { safeReturnUrl } from './return-url';

describe('safeReturnUrl', () => {
  it('accepts same-app paths', () => {
    expect(safeReturnUrl('/events/abc')).toBe('/events/abc');
    expect(safeReturnUrl('/events/abc?x=1')).toBe('/events/abc?x=1');
  });

  it.each([
    'https://evil.example',
    '//evil.example',
    '/\\evil.example',
    'javascript:alert(1)',
    'events/abc',
    '',
    null,
    undefined,
  ])('falls back to home for %s', (value) => {
    expect(safeReturnUrl(value as string | null | undefined)).toBe('/');
  });
});

describe('Login and Register pages', () => {
  const auth = {
    user: signal(null),
    login: vi.fn(),
    register: vi.fn(),
  };

  function setup(returnUrl: string | null = null) {
    auth.login.mockReset();
    auth.register.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: { get: () => returnUrl } } },
        },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    return navigate;
  }

  function type(el: HTMLElement, name: string, value: string) {
    const input = el.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  const submit = (el: HTMLElement) =>
    el.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));

  it('login: submits credentials and navigates to the validated return URL', async () => {
    const navigate = setup('/events/abc');
    auth.login.mockReturnValue(of({ id: '1' }));
    const fixture = TestBed.createComponent(Login);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'email', ' a@example.com ');
    type(el, 'password', 'a-long-password-1');
    submit(el);
    expect(auth.login).toHaveBeenCalledWith('a@example.com', 'a-long-password-1');
    expect(navigate).toHaveBeenCalledWith('/events/abc');
  });

  it('login: ignores an external return URL', async () => {
    const navigate = setup('https://evil.example');
    auth.login.mockReturnValue(of({ id: '1' }));
    const fixture = TestBed.createComponent(Login);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'email', 'a@example.com');
    type(el, 'password', 'a-long-password-1');
    submit(el);
    expect(navigate).toHaveBeenCalledWith('/');
  });

  it('login: shows the server error, clears the password and stays on the page', async () => {
    const navigate = setup();
    auth.login.mockReturnValue(
      throwError(
        () =>
          new ApiError(
            'problem',
            401,
            'Identity.InvalidCredentials',
            'x',
            'The email or password is incorrect.',
          ),
      ),
    );
    const fixture = TestBed.createComponent(Login);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'email', 'a@example.com');
    type(el, 'password', 'wrong-password-1');
    submit(el);
    await fixture.whenStable();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('incorrect');
    expect(navigate).not.toHaveBeenCalled();
    expect(el.querySelector<HTMLInputElement>('input[name="password"]')!.value).toBe('');
  });

  it('login: explains rate limiting', async () => {
    setup();
    auth.login.mockReturnValue(throwError(() => new ApiError('http', 429, null, null, 'x')));
    const fixture = TestBed.createComponent(Login);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'email', 'a@example.com');
    type(el, 'password', 'whatever-123456');
    submit(el);
    await fixture.whenStable();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Too many attempts');
  });

  it('login: does not call the API with empty fields', async () => {
    setup();
    const fixture = TestBed.createComponent(Login);
    await fixture.whenStable();
    submit(fixture.nativeElement as HTMLElement);
    expect(auth.login).not.toHaveBeenCalled();
  });

  it('register: enforces the 12 character minimum before calling the API', async () => {
    setup();
    const fixture = TestBed.createComponent(Register);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'displayName', 'Ada');
    type(el, 'email', 'a@example.com');
    type(el, 'password', 'short');
    submit(el);
    await fixture.whenStable();
    expect(auth.register).not.toHaveBeenCalled();
    expect(el.querySelector('.field__error')?.textContent).toContain('at least 12');
  });

  it('register: submits and navigates on success', async () => {
    const navigate = setup('/events/abc');
    auth.register.mockReturnValue(of({ id: '1' }));
    const fixture = TestBed.createComponent(Register);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'displayName', 'Ada');
    type(el, 'email', 'a@example.com');
    type(el, 'password', 'a-long-password-1');
    submit(el);
    expect(auth.register).toHaveBeenCalledWith('a@example.com', 'a-long-password-1', 'Ada');
    expect(navigate).toHaveBeenCalledWith('/events/abc');
  });

  it('register: shows a duplicate-email conflict from the server', async () => {
    setup();
    auth.register.mockReturnValue(
      throwError(
        () =>
          new ApiError('problem', 409, 'Identity.EmailTaken', 'x', 'An account may already exist.'),
      ),
    );
    const fixture = TestBed.createComponent(Register);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'displayName', 'Ada');
    type(el, 'email', 'a@example.com');
    type(el, 'password', 'a-long-password-1');
    submit(el);
    await fixture.whenStable();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('may already exist');
  });
});
