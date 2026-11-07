import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';
import { problemDetailsInterceptor } from './core/api/problem-details.interceptor';
import { LiveService } from './core/live/live.service';
import { serverClockInterceptor } from './core/time/server-clock';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // The session (and CSRF cookie) is known before the first route renders, so a refresh never flashes signed-out.
    // The live connection opens after the session is known (it is authenticated by the session cookie) and is not waited for:
    // the first route renders without it, and every page works with or without it.
    provideAppInitializer(() => {
      const live = inject(LiveService);
      return inject(AuthService)
        .load()
        .then(() => live.start());
    }),
    provideHttpClient(
      withInterceptors([serverClockInterceptor, authInterceptor, problemDetailsInterceptor]),
    ),
  ],
};
