import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Password reset is Entra self-service password reset: this app never sees or stores a password. */
@Component({
  selector: 'app-forgot-password',
  imports: [RouterLink],
  template: `
    <p class="eyebrow">ACCOUNT HELP</p>
    <h1 class="auth-title">Reset your password</h1>
    <p class="auth-lead">Your password belongs to your Microsoft work account, so you reset it with Microsoft, not here.</p>
    <div class="auth-body">
      <ul>
        <li>Use Microsoft's self-service password reset if your organization has turned it on.</li>
        <li>Otherwise, contact your IT help desk.</li>
      </ul>
    </div>
    <div class="auth-actions">
      <a class="button" href="https://passwordreset.microsoftonline.com/" target="_blank" rel="noopener noreferrer">Reset password with Microsoft</a>
    </div>
    <nav class="auth-links" aria-label="Account help"><a routerLink="/login">Back to sign in</a></nav>
  `,
})
export class ForgotPassword {}
