import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StatusKind, statusView } from '../../presentation';

/** Shows a backend status with the shared label and tone for its state machine. */
@Component({
  selector: 'app-status-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span
    class="badge"
    [class]="'badge badge--' + view().tone"
    [attr.data-status]="value()"
    >{{ view().label }}</span
  >`,
})
export class StatusBadge {
  readonly kind = input.required<StatusKind>();
  readonly value = input.required<string | null | undefined>();
  /** False for a payment through a real provider, so its labels never say "(simulated)". */
  readonly simulated = input(true);
  protected readonly view = computed(() => statusView(this.kind(), this.value(), this.simulated()));
}
