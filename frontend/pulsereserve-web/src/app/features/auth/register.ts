import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { toApiError } from '../../core/api/api-error';
import { AuthService } from '../../core/auth/auth.service';
import { Alert } from '../../shared/ui/alert/alert';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { MIN_PASSWORD_LENGTH, isPlausibleEmail, passwordHint } from './auth-form';
import { authErrorMessage } from './login';
import { safeReturnUrl } from './return-url';

@Component({
  selector: 'app-register',
  imports: [RouterLink, Alert],
  template: `
    <div class="container container--narrow page">
      <div class="card">
        <div class="card__body stack auth-card">
          <header class="stack stack--tight">
            <h1 class="auth-title">Create your account</h1>
            <p class="muted">One account to hold seats, pay and keep your tickets.</p>
          </header>

          @if (error(); as message) {
            <app-alert tone="error" heading="We could not create your account">{{
              message
            }}</app-alert>
          }

          <form class="stack" (submit)="submit($event)" novalidate>
            <div class="field">
              <label class="field__label" for="reg-name">Name</label>
              <input
                id="reg-name"
                class="input"
                type="text"
                name="displayName"
                autocomplete="name"
                maxlength="100"
                required
                [attr.aria-invalid]="nameError() ? 'true' : null"
                [attr.aria-describedby]="nameError() ? 'reg-name-error' : null"
                [value]="displayName()"
                (input)="displayName.set($any($event.target).value)"
                (blur)="nameTouched.set(true)"
              />
              @if (nameError(); as e) {
                <p class="field__error" id="reg-name-error">{{ e }}</p>
              }
            </div>

            <div class="field">
              <label class="field__label" for="reg-email">Email</label>
              <input
                id="reg-email"
                class="input"
                type="email"
                name="email"
                autocomplete="username"
                required
                [attr.aria-invalid]="emailError() ? 'true' : null"
                [attr.aria-describedby]="emailError() ? 'reg-email-error' : null"
                [value]="email()"
                (input)="email.set($any($event.target).value)"
                (blur)="emailTouched.set(true)"
              />
              @if (emailError(); as e) {
                <p class="field__error" id="reg-email-error">{{ e }}</p>
              }
            </div>

            <div class="field">
              <label class="field__label" for="reg-password">Password</label>
              <div class="field__control">
                <input
                  id="reg-password"
                  class="input input--with-action"
                  [type]="reveal() ? 'text' : 'password'"
                  #pw
                  name="password"
                  autocomplete="new-password"
                  minlength="12"
                  maxlength="128"
                  required
                  aria-describedby="reg-password-hint"
                  [attr.aria-invalid]="passwordError() ? 'true' : null"
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
              <p class="field__hint" id="reg-password-hint">
                At least 12 characters.
                @if (hint(); as h) {
                  <strong class="hint hint--{{ h.tone }}">{{ h.label }}</strong>
                }
              </p>
              @if (passwordError(); as e) {
                <p class="field__error">{{ e }}</p>
              }
            </div>

            <button
              type="submit"
              class="btn btn--primary btn--block"
              [class.is-loading]="busy()"
              [disabled]="busy()"
            >
              {{ busy() ? 'Creating account…' : 'Create account' }}
            </button>
          </form>

          <p class="muted auth-switch">
            Already registered?
            <a routerLink="/login" [queryParams]="{ returnUrl: returnUrl }">Sign in</a>
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
    .hint {
      margin-left: var(--space-1);
    }
    .hint--danger {
      color: var(--danger);
    }
    .hint--warning {
      color: var(--warning);
    }
    .hint--success {
      color: var(--success);
    }
  `,
})
export class Register {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);
  protected readonly returnUrl = safeReturnUrl(
    inject(ActivatedRoute).snapshot.queryParamMap.get('returnUrl'),
  );

  protected readonly displayName = signal('');
  protected readonly email = signal('');
  protected readonly password = signal('');
  protected readonly nameTouched = signal(false);
  protected readonly emailTouched = signal(false);
  protected readonly passwordTouched = signal(false);
  protected readonly reveal = signal(false);
  private readonly passwordInput = viewChild<ElementRef<HTMLInputElement>>('pw');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected hint(): ReturnType<typeof passwordHint> {
    // The hint guides while typing; once the field shows its own error the hint stays quiet (no duplicate message).
    return (this.passwordTouched() || this.password().length > 0) && !this.passwordError()
      ? passwordHint(this.password())
      : null;
  }

  protected nameError(): string | null {
    return this.nameTouched() && !this.displayName().trim() ? 'Enter your name.' : null;
  }

  protected emailError(): string | null {
    return this.emailTouched() && !isPlausibleEmail(this.email())
      ? 'Enter a valid email address.'
      : null;
  }

  protected passwordError(): string | null {
    return this.passwordTouched() && this.password().length < MIN_PASSWORD_LENGTH
      ? `The password must be at least ${MIN_PASSWORD_LENGTH} characters long.`
      : null;
  }

  protected submit(event: Event): void {
    event.preventDefault();
    if (this.busy()) {
      return;
    }
    this.nameTouched.set(true);
    this.emailTouched.set(true);
    this.passwordTouched.set(true);
    if (this.nameError() || this.emailError() || this.passwordError()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.auth.register(this.email().trim(), this.password(), this.displayName().trim()).subscribe({
      next: (user) => {
        this.clearPassword();
        this.toasts.success(`Welcome, ${user.displayName}. Your account is ready.`);
        void this.router.navigateByUrl(this.returnUrl);
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(authErrorMessage(toApiError(e)));
      },
    });
  }

  private clearPassword(): void {
    this.password.set('');
    const input = this.passwordInput()?.nativeElement;
    if (input) {
      input.value = '';
    }
  }
}
