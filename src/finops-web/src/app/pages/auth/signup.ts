import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * Request access. There is no self-service sign-up: accounts live in the organization's Entra tenant, and an
 * administrator provisions each person's FinOps profile, roles and departments (ADR 0002).
 */
@Component({
  selector: 'app-signup',
  imports: [RouterLink],
  template: `
    <p class="eyebrow">NEW TO FINOPS</p>
    <h1 class="auth-title">Request access</h1>
    <p class="auth-lead">FinOps Enterprise uses your organization's Microsoft account, so there's no separate password to create.</p>
    <div class="auth-body">
      <ol>
        <li>Sign in with your Microsoft work account.</li>
        <li>If you don't have access yet, you'll see your account's <strong>Object ID</strong>.</li>
        <li>Send it to your FinOps administrator. They'll assign your role and departments.</li>
      </ol>
    </div>
    <div class="auth-actions"><a class="button" routerLink="/login">Continue to sign in</a></div>
    <nav class="auth-links" aria-label="Account help"><a routerLink="/login">Already have access? Sign in</a></nav>
  `,
})
export class Signup {}
