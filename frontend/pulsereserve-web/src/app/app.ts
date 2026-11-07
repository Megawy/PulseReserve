import {
  Component,
  ElementRef,
  HostListener,
  computed,
  inject,
  isDevMode,
  signal,
  viewChild,
} from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { AuthService } from './core/auth/auth.service';
import { ToastHost } from './shared/ui/toast/toast-host';
import { ToastService } from './shared/ui/toast/toast.service';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ToastHost],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);
  private readonly main = viewChild.required<ElementRef<HTMLElement>>('main');

  protected readonly menuOpen = signal(false);
  protected readonly devBuild = isDevMode();
  protected readonly initial = computed(() =>
    (this.auth.user()?.displayName.trim()[0] ?? '?').toUpperCase(),
  );

  constructor() {
    // The mobile menu always closes after navigating.
    this.router.events
      .pipe(filter((e) => e instanceof NavigationEnd))
      .subscribe(() => this.menuOpen.set(false));
  }

  @HostListener('document:keydown.escape')
  protected closeMenuOnEscape(): void {
    this.menuOpen.set(false);
  }

  /**
   * Skip link handler. A plain `href="#main-content"` would resolve against `<base href="/">`
   * and navigate away from the current route, so focus is moved programmatically instead.
   */
  protected skipToMain(event: Event): void {
    event.preventDefault();
    this.main().nativeElement.focus();
  }

  protected signOut(): void {
    this.menuOpen.set(false);
    this.auth.logout().subscribe({
      next: () => {
        this.toasts.success('You have been signed out.');
        void this.router.navigateByUrl('/');
      },
      // Even if the request failed the UI cannot trust the session any more; reload state from the server.
      error: () => void this.auth.load(),
    });
  }
}
