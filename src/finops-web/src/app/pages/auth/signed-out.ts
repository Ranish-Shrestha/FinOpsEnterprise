import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-signed-out',
  imports: [RouterLink],
  template: `
    <p class="eyebrow">SEE YOU SOON</p>
    <h1 class="auth-title">You're signed out</h1>
    <p class="auth-lead">This app's session has been cleared from this browser tab.</p>
    <div class="auth-actions"><a class="button" routerLink="/login">Sign in again</a></div>
  `,
})
export class SignedOut {}
