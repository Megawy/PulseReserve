import { Injectable, signal } from '@angular/core';

export type ToastTone = 'info' | 'success' | 'warning' | 'error';

export interface Toast {
  readonly id: number;
  readonly tone: ToastTone;
  readonly text: string;
}

/**
 * Transient confirmations ("Signed in", "Order cancelled"). Anything the user must act on, or that explains a state,
 * stays inline next to its cause; a toast is only a courtesy and disappears by itself (errors stay longer).
 */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 1;
  private readonly timers = new Map<number, ReturnType<typeof setTimeout>>();
  readonly toasts = signal<readonly Toast[]>([]);

  show(text: string, tone: ToastTone = 'info'): void {
    const id = this.nextId++;
    this.toasts.update((list) => [...list.slice(-3), { id, tone, text }]);
    this.timers.set(
      id,
      setTimeout(() => this.dismiss(id), tone === 'error' ? 9000 : 5000),
    );
  }

  success(text: string): void {
    this.show(text, 'success');
  }

  error(text: string): void {
    this.show(text, 'error');
  }

  dismiss(id: number): void {
    clearTimeout(this.timers.get(id));
    this.timers.delete(id);
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }
}
