import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiError, toApiError } from '../../core/api/api-error';
import { AuthService } from '../../core/auth/auth.service';
import { Alert } from '../../shared/ui/alert/alert';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { isPlausibleEmail } from './auth-form';
import { safeReturnUrl } from './return-url';

export function authErrorMessage(error: ApiError): string {
  if (error.status === 429) {
    return 'Too many attempts. Wait a minute and try again.';
  }
  return error.message;
}

@Component({
  selector: 'app-login',
  imports: [RouterLink, Alert],
  template: `
    <div class="container container--narrow page">
      <div class="card">
        <div class="card__body stack auth-card">
          <header class="stack stack--tight">
            <h1 class="auth-title">Welcome back</h1>
            <p class="muted">Sign in to hold seats, check out and see your tickets.</p>
          </header>

          @if (error(); as message) {
            <app-alert tone="error" heading="We could not sign you in">{{ message }}</app-alert>
          }

          <form class="stack" (submit)="submit($event)" novalidate>
            <div class="field">
              <label class="field__label" for="login-email">Email</label>
              <input
                id="login-email"
                class="input"
                type="email"
                name="email"
                autocomplete="username"
                required
                [attr.aria-invalid]="emailError() ? 'true' : null"
                [attr.aria-describedby]="emailError() ? 'login-email-error' : null"
                [value]="email()"
                (input)="email.set($any($event.target).value)"
                (blur)="emailTouched.set(true)"
              />
              @if (emailError(); as e) {
                <p class="field__error" id="login-email-error">{{ e }}</p>
              }
            </div>

            <div class="field">
              <label class="field__label" for="login-password">Password</label>
              <div class="field__control">
                <input
                  id="login-password"
                  class="input input--with-action"
                  [type]="reveal() ? 'text' : 'password'"
                  #pw
                  name="password"
                  autocomplete="current-password"
                  required
                  [attr.aria-invalid]="passwordError() ? 'true' : null"
                  [attr.aria-describedby]="passwordError() ? 'login-password-error' : null"
                  [value]="password()"
                  (input)="password.set($any($event.target).value)"
                  (blur)="passwordTouched.set(true)"
                />
                <button
                  type="button"
                  class="field__action"
                  [attr.aria-pressed]="reveal()"
                  (click)="reveal.set(!reveal())"
                >
                  {{ reveal() ? 'Hide' : 'Show' }}
                </button>
              </div>
              @if (passwordError(); as e) {
                <p class="field__error" id="login-password-error">{{ e }}</p>
              }
            </div>

            <button
              type="submit"
              class="btn btn--primary btn--block"
              [class.is-loading]="busy()"
              [disabled]="busy()"
            >
              {{ busy() ? 'Signing in…' : 'Sign in' }}
            </button>
          </form>

          <p class="muted auth-switch">
            New to PulseReserve?
            <a routerLink="/register" [queryParams]="{ returnUrl: returnUrl }">Create an account</a>
          </p>
        </div>
      </div>
    </div>
  `,
  styles: `
    .auth-title {
      font-size: var(--font-size-2xl);
    }
    .auth-switch {
      padding-top: var(--space-4);
      border-top: 1px solid var(--border-muted);
      text-align: center;
    }
  `,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);
  protected readonly returnUrl = safeReturnUrl(
    inject(ActivatedRoute).snapshot.queryParamMap.get('returnUrl'),
  );

  protected readonly email = signal('');
  protected readonly password = signal('');
  protected readonly emailTouched = signal(false);
  protected readonly passwordTouched = signal(false);
  protected readonly reveal = signal(false);
  private readonly passwordInput = viewChild<ElementRef<HTMLInputElement>>('pw');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected emailError(): string | null {
    if (!this.emailTouched()) {
      return null;
    }
    return isPlausibleEmail(this.email()) ? null : 'Enter a valid email address.';
  }

  protected passwordError(): string | null {
    return this.passwordTouched() && !this.password() ? 'Enter your password.' : null;
  }

  protected submit(event: Event): void {
    event.preventDefault();
    if (this.busy()) {
      return;
    }
    this.emailTouched.set(true);
    this.passwordTouched.set(true);
    if (!this.email().trim() || !this.password()) {
      this.error.set('Enter your email and password.');
      return;
    }
    if (!isPlausibleEmail(this.email())) {
      this.error.set(null);
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.auth.login(this.email().trim(), this.password()).subscribe({
      next: (user) => {
        this.clearPassword();
        this.toasts.success(`Signed in as ${user.displayName}.`);
        void this.router.navigateByUrl(this.returnUrl);
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.clearPassword();
        this.error.set(authErrorMessage(toApiError(e)));
      },
    });
  }

  /** Property binding alone would not clear the field (its bound value was already ''), so reset the element too. */
  private clearPassword(): void {
    this.password.set('');
    this.passwordTouched.set(false); // an emptied field after a failed attempt is not a user mistake to flag
    const input = this.passwordInput()?.nativeElement;
    if (input) {
      input.value = '';
    }
  }
}
