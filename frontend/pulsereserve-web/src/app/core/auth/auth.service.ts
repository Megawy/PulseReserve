import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of, tap } from 'rxjs';
import { API_BASE_URL } from '../config/app-config';

export interface AuthUser {
  readonly id: string;
  readonly email: string;
  readonly displayName: string;
}

/** GET/POST /api/identity/* response. The CSRF token travels in a cookie that Angular echoes back itself. */
interface SessionWire {
  readonly isAuthenticated: boolean;
  readonly user: AuthUser | null;
  readonly isAdmin?: boolean;
}

/**
 * Authentication state for the SPA. The session itself is an HttpOnly cookie managed by the server: JavaScript
 * never sees a password after submission, a token, or the session cookie, and nothing is kept in browser storage.
 * This state is only a UI mirror of what `GET /api/identity/session` says; the server decides on every request.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(API_BASE_URL)}/identity`;

  private readonly userState = signal<AuthUser | null>(null);
  private readonly expiredState = signal(false);
  private readonly adminState = signal(false);

  readonly user = this.userState.asReadonly();
  readonly isAuthenticated = computed(() => this.userState() !== null);
  /**
   * UI hint only (e.g. to label the account). Admin rights are granted by deployment configuration and enforced by
   * the server on every request; hiding or showing anything here is never a security boundary.
   */
  readonly isAdmin = this.adminState.asReadonly();
  /** True after a protected request answered 401 while the UI believed it was signed in. */
  readonly sessionExpired = this.expiredState.asReadonly();

  /** Loads the current session (and the CSRF cookie). Never rejects: failure means "signed out". */
  load(): Promise<void> {
    removeLegacyDevIdentity();
    return firstValueFrom(
      this.http.get<SessionWire>(`${this.base}/session`).pipe(
        tap((s) => this.apply(s)),
        map(() => undefined),
        catchError(() => {
          this.userState.set(null);
          return of(undefined);
        }),
      ),
    );
  }

  login(email: string, password: string): Observable<AuthUser> {
    return this.http
      .post<SessionWire>(`${this.base}/login`, { email, password })
      .pipe(map((s) => this.applyAuthenticated(s)));
  }

  register(email: string, password: string, displayName: string): Observable<AuthUser> {
    return this.http
      .post<SessionWire>(`${this.base}/register`, { email, password, displayName })
      .pipe(map((s) => this.applyAuthenticated(s)));
  }

  logout(): Observable<void> {
    return this.http.post<SessionWire>(`${this.base}/logout`, null).pipe(
      tap((s) => this.apply(s)),
      map(() => undefined),
    );
  }

  /** Called by the HTTP interceptor when a protected call answers 401. */
  handleUnauthorized(): void {
    if (this.userState() !== null) {
      this.userState.set(null);
      this.adminState.set(false);
      this.expiredState.set(true);
    }
  }

  private applyAuthenticated(session: SessionWire): AuthUser {
    this.apply(session);
    const user = this.userState();
    if (!user) {
      throw new Error('The server did not return a signed-in user.');
    }
    return user;
  }

  private apply(session: SessionWire): void {
    this.userState.set(session.isAuthenticated ? session.user : null);
    this.adminState.set(session.isAuthenticated && session.isAdmin === true);
    this.expiredState.set(false);
  }
}

/** The development-only client-side customer ID of earlier builds is gone; drop any leftover value. */
function removeLegacyDevIdentity(): void {
  try {
    globalThis.localStorage?.removeItem('pulsereserve.dev.customerId');
  } catch {
    // Storage unavailable: nothing to clean up.
  }
}
