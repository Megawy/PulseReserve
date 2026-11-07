import { Component, computed, input, output } from '@angular/core';
import { PAGE_SIZES } from './ops-flow';

export interface FilterOption {
  readonly value: string;
  readonly label: string;
}

/** State filter and page size are sent to the API; the search box narrows only the rows already loaded. */
@Component({
  selector: 'app-ops-toolbar',
  template: `
    <form class="toolbar" role="search" (submit)="$event.preventDefault()">
      @if (showFilter()) {
        <div class="field">
          <label class="field__label" [for]="id() + '-state'">{{ filterLabel() }}</label>
          <select
            class="select"
            [id]="id() + '-state'"
            [value]="filter() ?? ''"
            (change)="filterChange.emit(valueOf($event) || null)"
          >
            <option value="">All</option>
            @for (option of options(); track option.value) {
              <option [value]="option.value" [selected]="option.value === filter()">
                {{ option.label }}
              </option>
            }
          </select>
        </div>
      }
      <div class="field field--grow">
        <label class="field__label" [for]="id() + '-search'">Search loaded rows</label>
        <input
          class="input"
          type="search"
          [id]="id() + '-search'"
          [value]="query()"
          placeholder="Id, type or message"
          autocomplete="off"
          (input)="queryChange.emit(valueOf($event))"
        />
      </div>
      <div class="field">
        <label class="field__label" [for]="id() + '-limit'">{{
          unresolvedFirst() ? 'Show up to' : 'Newest'
        }}</label>
        <select class="select" [id]="id() + '-limit'" (change)="limitChange.emit(+valueOf($event))">
          @for (size of sizes; track size) {
            <option [value]="size" [selected]="size === limit()">{{ size }}</option>
          }
        </select>
      </div>
      <button type="button" class="btn btn--secondary" (click)="refresh.emit()">Refresh</button>
    </form>
    <p class="subtle summary" role="status">{{ summary() }}</p>
  `,
  styles: `
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-end;
      gap: var(--space-3);
    }
    .field {
      min-width: 9rem;
    }
    .field--grow {
      flex: 1 1 14rem;
    }
    .summary {
      margin-block: var(--space-2) var(--space-4);
    }
  `,
})
export class OpsToolbar {
  readonly id = input.required<string>();
  readonly filterLabel = input('State');
  readonly showFilter = input(true);
  readonly options = input.required<readonly FilterOption[]>();
  readonly filter = input<string | null>(null);
  readonly query = input('');
  readonly limit = input<number>(PAGE_SIZES[0]);
  readonly shown = input(0);
  readonly loaded = input(0);
  readonly noun = input('items');
  /**
   * True for lists where unresolved items come first, oldest first, then resolved ones newest first (payment exceptions and anomalies):
   * the longest-waiting item can never be pushed off the page by newer ones, and the wording must not say "newest first".
   */
  readonly unresolvedFirst = input(false);
  readonly filterChange = output<string | null>();
  readonly queryChange = output<string>();
  readonly limitChange = output<number>();
  readonly refresh = output<void>();

  protected readonly sizes = PAGE_SIZES;
  protected readonly summary = computed(() => {
    const order = this.unresolvedFirst()
      ? 'open ones first, oldest first, then resolved'
      : 'newest first';
    const base = `Showing ${this.shown()} of ${this.loaded()} loaded ${this.noun()} (${order}, up to ${this.limit()}).`;
    return this.loaded() >= this.limit()
      ? `${base} There may be ${this.unresolvedFirst() ? 'more' : 'older ones'}: raise the limit to see more.`
      : base;
  });

  protected valueOf(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement).value;
  }
}
