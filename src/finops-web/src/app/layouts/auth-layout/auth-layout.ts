import { Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';

/** Sign-in and account-help pages: a centered card with the brand, no workspace navigation. */
@Component({
  selector: 'app-auth-layout',
  imports: [RouterOutlet, RouterLink],
  template: `
    <div class="auth-page">
      <a class="brand" routerLink="/login" aria-label="FinOps Enterprise sign in">
        <span class="brand-icon" aria-hidden="true">F</span>
        <span>FinOps<span class="brand-subtitle">ENTERPRISE</span></span>
      </a>
      <main id="main" class="auth-card"><router-outlet /></main>
      <p class="auth-footer">Sign-in is handled by Microsoft or Google. This app never stores your password.</p>
    </div>
  `,
  styles: `
    .auth-page { min-height: 100vh; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 28px; padding: 40px 16px; background: #122b2a; }
    .brand { display: flex; align-items: center; gap: 12px; color: white; text-decoration: none; font-size: 25px; font-weight: 650; letter-spacing: -1px; }
    .brand-icon { display: grid; place-items: center; width: 39px; height: 43px; border: 1px solid #6a8e83; border-radius: 10px; color: #b9e6cf; font-family: Georgia, serif; font-size: 30px; }
    .brand-subtitle { display: block; font-size: 9px; letter-spacing: 3px; margin-top: 4px; color: #a3beb5; }
    .auth-card { width: 100%; max-width: 440px; background: #fff; border-radius: 12px; padding: 36px; box-shadow: 0 18px 50px rgb(0 0 0 / 25%); }
    .auth-footer { color: #a3beb5; font-size: 11px; text-align: center; max-width: 440px; margin: 0; }
    @media (max-width: 480px) { .auth-card { padding: 26px 20px; } }
  `,
})
export class AuthLayout {}
