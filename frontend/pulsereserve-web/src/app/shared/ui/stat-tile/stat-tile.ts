import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * One real number from the API with a label and an optional destination. `tone` marks the value as needing attention
 * (a tile for "needs review" is only highlighted when its count is above zero, so colour never stands in for data).
 */
@Component({
  selector: 'app-stat-tile',
  imports: [RouterLink, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    :host {
      display: grid;
    }
  `,
  template: `
    @if (link(); as target) {
      <a
        class="tile tile--link"
        [class.tile--attention]="attention()"
        [routerLink]="target"
        [queryParams]="queryParams()"
      >
        <ng-container *ngTemplateOutlet="body" />
      </a>
    } @else {
      <div class="tile" [class.tile--attention]="attention()">
        <ng-container *ngTemplateOutlet="body" />
      </div>
    }
    <ng-template #body>
      <span class="tile__label">{{ label() }}</span>
      <span class="tile__value num">{{ value() }}</span>
      @if (hint()) {
        <span class="tile__hint">{{ hint() }}</span>
      }
    </ng-template>
  `,
})
export class StatTile {
  readonly label = input.required<string>();
  readonly value = input.required<number | string>();
  readonly hint = input('');
  readonly link = input<string | readonly string[] | null>(null);
  readonly queryParams = input<Record<string, string> | null>(null);
  readonly attention = input(false);
}
