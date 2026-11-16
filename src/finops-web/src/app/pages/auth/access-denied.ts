import { Component, computed, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../auth.service';
import { SessionStore } from '../../core/session.store';

/** Signed in, but the API has no active FinOps profile for this account (`/api/me` answered 403). */
@Component({
  selector: 'app-access-denied',
  imports: [RouterLink],
  template: `
    <p class="eyebrow">NO ACCESS YET</p>
    <h1 class="auth-title">You don't have access</h1>
    @if (auth.authenticated()) {
      <p class="auth-lead">You're signed in, but your account isn't set up in FinOps Enterprise, or it's no longer active.</p>
      @if (objectId(); as id) {
        <p class="auth-note">Send this Object ID to your FinOps administrator:<br><code>{{ id }}</code></p>
      }
      <div class="auth-actions">
        <button type="button" (click)="retry()">Try again</button>
        <button type="button" class="button secondary" (click)="signOut()">Sign out</button>
      </div>
    } @else {
      <p class="auth-lead">Sign in to continue.</p>
      <div class="auth-actions"><a class="button" routerLink="/login">Sign in</a></div>
    }
  `,
})
export class AccessDenied {
  private readonly router = inject(Router);
  private readonly session = inject(SessionStore);
  readonly auth = inject(AuthService);
  /** From the API, which knows Google accounts' IDs too; else read from the Entra token. */
  readonly objectId = computed(() => this.session.state()?.signIn?.accountId ?? this.auth.accountObjectId());

  async retry(): Promise<void> {
    await this.session.refresh();
    await this.router.navigateByUrl('/dashboard');
  }

  signOut(): void {
    const leftApp = this.auth.signOut();
    this.session.clear();
    if (!leftApp) void this.router.navigateByUrl('/signed-out');
  }
}
