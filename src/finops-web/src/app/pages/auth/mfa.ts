import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** MFA is enforced by Entra (security defaults or Conditional Access) during the Microsoft sign-in. */
@Component({
  selector: 'app-mfa',
  imports: [RouterLink],
  template: `
    <p class="eyebrow">ACCOUNT SECURITY</p>
    <h1 class="auth-title">Multi-factor authentication</h1>
    <p class="auth-lead">Microsoft Entra ID asks for your second factor during sign-in, before you return to FinOps Enterprise.</p>
    <div class="auth-body">
      <ul>
        <li>Your organization's policy decides when MFA is required.</li>
        <li>Set up or change your methods (Authenticator app, phone or security key) in your Microsoft security info.</li>
        <li>Lost your device? Contact your IT help desk to reset your methods.</li>
      </ul>
    </div>
    <div class="auth-actions">
      <a class="button" href="https://mysignins.microsoft.com/security-info" target="_blank" rel="noopener noreferrer">Manage security info</a>
    </div>
    <nav class="auth-links" aria-label="Account help"><a routerLink="/login">Back to sign in</a></nav>
  `,
})
export class Mfa {}
