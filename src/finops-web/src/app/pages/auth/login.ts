import { Component, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../auth.service';
import { SessionStore } from '../../core/session.store';

/**
 * Sign in. With Entra, this is single sign-on: Microsoft's page handles the password, MFA and any Conditional Access
 * policy, then returns here. In local development, a synthetic persona stands in for the Entra account.
 */
@Component({
  selector: 'app-login',
  imports: [RouterLink],
  template: `
    <p class="eyebrow">WELCOME BACK</p>
    <h1 class="auth-title">Sign in</h1>
    <p class="auth-lead">
      @if (auth.mockMode) { Local development: choose a synthetic persona. No real account is used. }
      @else { Use your organization's Microsoft account. Your password and MFA are handled by Microsoft Entra ID. }
    </p>
    @if (auth.signInError()) { <p class="auth-error" role="alert">{{ auth.signInError() }}</p> }
    <div class="auth-actions">
      @if (auth.mockMode) {
        <label class="auth-field">Development persona
          <select [value]="auth.persona()" (change)="auth.selectPersona($any($event.target).value)">
            @for (persona of auth.personas; track persona.objectId) {
              <option [value]="persona.objectId">{{ persona.label }}</option>
            }
          </select>
        </label>
        <button type="button" (click)="continueAsPersona()" [disabled]="busy()">{{ busy() ? 'Signing in...' : 'Continue' }}</button>
      } @else {
        <button type="button" (click)="signInWithMicrosoft()" [disabled]="busy()">{{ busy() ? 'Redirecting to Microsoft...' : 'Sign in with Microsoft' }}</button>
      }
    </div>
    <nav class="auth-links" aria-label="Account help">
      <a routerLink="/forgot-password">Forgot password?</a>
      <a routerLink="/mfa">About MFA</a>
      <a routerLink="/signup">Request access</a>
    </nav>
  `,
})
export class Login {
  private readonly router = inject(Router);
  private readonly session = inject(SessionStore);
  readonly auth = inject(AuthService);

  /** `?returnUrl=` set by the auth guard. AuthService accepts only same-origin paths. */
  readonly returnUrl = input<string | undefined>();
  readonly busy = signal(false);

  async continueAsPersona(): Promise<void> {
    this.busy.set(true);
    try {
      await this.auth.signIn(this.returnUrl());
      await this.session.refresh();
      await this.router.navigateByUrl(this.auth.consumeReturnUrl());
    } finally {
      this.busy.set(false);
    }
  }

  async signInWithMicrosoft(): Promise<void> {
    this.busy.set(true);
    try {
      await this.auth.signIn(this.returnUrl()); // leaves for Entra
    } catch {
      this.auth.signInError.set('Could not start sign-in. Please try again.');
      this.busy.set(false);
    }
  }
}
