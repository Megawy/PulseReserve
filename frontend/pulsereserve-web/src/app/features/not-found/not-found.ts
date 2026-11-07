import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-not-found',
  imports: [RouterLink],
  template: `
    <h1>Page not found</h1>
    <p>The page you requested does not exist.</p>
    <p><a routerLink="/">Go to the home page</a></p>
  `,
})
export class NotFound {}
