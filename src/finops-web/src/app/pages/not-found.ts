import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-not-found',
  imports: [RouterLink],
  template: `
    <p class="eyebrow">404</p>
    <h1 class="auth-title">Page not found</h1>
    <p class="auth-lead">That address doesn't match a page in FinOps Enterprise.</p>
    <div class="auth-actions"><a class="button" routerLink="/dashboard">Go to the workspace</a></div>
  `,
})
export class NotFound {}
