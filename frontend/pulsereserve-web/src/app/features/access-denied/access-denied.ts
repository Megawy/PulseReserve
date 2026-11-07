import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Shown to a signed-in account that is not an administrator. There is deliberately no way to request a role here. */
@Component({
  selector: 'app-access-denied',
  imports: [RouterLink],
  template: `
    <div class="container container--narrow page">
      <section class="state" aria-labelledby="denied-heading">
        <span class="state__icon" aria-hidden="true">!</span>
        <h1 id="denied-heading">Administrator access required</h1>
        <p>
          This area is limited to PulseReserve operators. Your account is signed in but does not
          have administrator access. Access is granted by the deployment, not from this page.
        </p>
        <a class="btn btn--primary" routerLink="/">Back to events</a>
      </section>
    </div>
  `,
})
export class AccessDenied {}
