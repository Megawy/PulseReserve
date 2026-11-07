import { Component, input, output } from '@angular/core';

/** Failure state with an optional retry (only offered when retrying is reasonable, see ApiError.retryable). */
@Component({
  selector: 'app-error-state',
  template: `
    <div class="state state--error" role="alert">
      <span class="state__icon" aria-hidden="true">!</span>
      <h2>{{ heading() }}</h2>
      <p>{{ message() }}</p>
      <div class="cluster">
        @if (retryable()) {
          <button type="button" class="btn btn--primary" (click)="retry.emit()">Try again</button>
        }
        <ng-content />
      </div>
    </div>
  `,
})
export class ErrorState {
  readonly heading = input('Something went wrong');
  readonly message = input.required<string>();
  readonly retryable = input(false);
  readonly retry = output<void>();
}
