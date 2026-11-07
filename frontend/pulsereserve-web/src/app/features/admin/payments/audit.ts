import { Component, inject } from '@angular/core';
import { AdminPaymentsApiClient } from '../../../core/api/admin-payments-api.client';
import { AuditEntry } from '../../../core/api/admin-payments.models';
import { formatDateTime, shortId } from '../../../shared/format';
import { auditActionLabel, subjectTypeLabel } from '../../../shared/presentation';
import { EmptyState } from '../../../shared/ui/empty-state/empty-state';
import { ErrorState } from '../../../shared/ui/error-state/error-state';
import { createOpsList } from './ops-flow';
import { OpsToolbar } from './ops-toolbar';

/**
 * Read-only history of administrator actions. The log is append-only on the server (database triggers refuse edits and
 * deletes) and this page offers no way to change it. Only the fields the API returns for each entry are shown.
 */
@Component({
  selector: 'app-admin-audit',
  imports: [EmptyState, ErrorState, OpsToolbar],
  template: `
    <section class="stack" aria-labelledby="audit-heading">
      <div class="stack stack--tight">
        <h2 id="audit-heading">Audit log</h2>
        <p class="muted">
          Who changed what, newest first. Entries are append-only: they cannot be edited or deleted,
          here or anywhere else.
        </p>
      </div>

      <app-ops-toolbar
        id="audit"
        filterLabel="Filter"
        noun="entries"
        [options]="[]"
        [showFilter]="false"
        [query]="list.query()"
        [limit]="list.limit()"
        [shown]="list.visible().length"
        [loaded]="list.loadedCount()"
        (queryChange)="list.setQuery($event)"
        (limitChange)="list.setLimit($event)"
        (refresh)="list.reload()"
      />

      @switch (list.state().kind) {
        @case ('loading') {
          <p class="visually-hidden" role="status">Loading the audit log…</p>
          <div class="panel stack" aria-hidden="true">
            @for (n of [1, 2, 3, 4]; track n) {
              <span class="skeleton skeleton--line"></span>
            }
          </div>
        }
        @case ('error') {
          @if (errorOf(); as err) {
            <app-error-state
              heading="We could not load the audit log"
              [message]="err.message"
              [retryable]="err.retryable"
              (retry)="list.reload()"
            />
          }
        }
        @default {
          @if (list.visible().length === 0) {
            <app-empty-state
              icon="✓"
              [heading]="
                list.loadedCount() === 0 ? 'No audit entries yet' : 'No entries match your search'
              "
              [message]="
                list.loadedCount() === 0
                  ? 'Entries appear when an administrator retries or requests something.'
                  : 'Clear the search box to see all loaded entries.'
              "
            />
          } @else {
            <div class="table-wrap">
              <table class="table table--stack">
                <caption class="visually-hidden">
                  Administrator actions, newest first
                </caption>
                <thead>
                  <tr>
                    <th scope="col">When</th>
                    <th scope="col">Administrator</th>
                    <th scope="col">Action</th>
                    <th scope="col">Subject</th>
                    <th scope="col">Detail</th>
                  </tr>
                </thead>
                <tbody>
                  @for (entry of list.visible(); track entry.id) {
                    <tr>
                      <td data-label="When">{{ when(entry.createdAtUtc) }}</td>
                      <td data-label="Administrator">
                        <code [title]="entry.actorUserId">{{ short(entry.actorUserId) }}</code>
                      </td>
                      <td data-label="Action">{{ action(entry.action) }}</td>
                      <td data-label="Subject">
                        {{ subject(entry.subjectType) }}
                        <code [title]="entry.subjectId">{{ short(entry.subjectId) }}</code>
                      </td>
                      <td data-label="Detail">{{ entry.detail ?? '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      }
    </section>
  `,
})
export class Audit {
  private readonly api = inject(AdminPaymentsApiClient);

  protected readonly list = createOpsList<AuditEntry>({
    allowed: [],
    fetch: (_filter, limit) => this.api.audit(limit),
    searchText: (e) =>
      [
        e.actorUserId,
        e.action,
        auditActionLabel(e.action),
        e.subjectType,
        e.subjectId,
        e.detail,
      ].join(' '),
  });

  protected readonly short = shortId;
  protected readonly when = formatDateTime;
  protected readonly action = auditActionLabel;
  protected readonly subject = subjectTypeLabel;

  protected errorOf() {
    const s = this.list.state();
    return s.kind === 'error' ? s.error : null;
  }
}
