import { Component, input } from '@angular/core';

/** Deliberate "nothing here" state with room for a call to action (projected content). */
@Component({
  selector: 'app-empty-state',
  template: `
    <section class="state" [attr.aria-label]="heading()">
      <span class="state__icon" aria-hidden="true">{{ icon() }}</span>
      <h2>{{ heading() }}</h2>
      <p>{{ message() }}</p>
      <ng-content />
    </section>
  `,
})
export class EmptyState {
  readonly heading = input.required<string>();
  readonly message = input.required<string>();
  readonly icon = input('✦');
}
