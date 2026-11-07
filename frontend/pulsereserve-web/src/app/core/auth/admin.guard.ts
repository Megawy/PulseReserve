import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/**
 * Keeps non-administrators out of the operations console. Anonymous visitors go to sign-in and come back afterwards;
 * signed-in non-admins see the access-denied page rather than an admin shell with failing calls. `isAdmin` is the
 * server's own answer from `GET /api/identity/session` (the browser never supplies or promotes a role), and this
 * guard is only a courtesy: every `/api/admin/*` request is authorised again by the API.
 */
export const adminGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }
  return auth.isAdmin() ? true : router.createUrlTree(['/access-denied']);
};
