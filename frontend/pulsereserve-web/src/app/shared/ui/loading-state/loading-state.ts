import { Component, input } from '@angular/core';

@Component({
  selector: 'app-loading-state',
  template: `
    <div class="state" role="status" aria-live="polite">
      <span class="spinner" aria-hidden="true"></span>
      <p>{{ message() }}</p>
    </div>
  `,
})
export class LoadingState {
  readonly message = input('Loading…');
}
