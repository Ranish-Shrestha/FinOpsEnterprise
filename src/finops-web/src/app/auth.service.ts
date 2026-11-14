import { Injectable, signal } from '@angular/core';
import { authConfig } from './auth.config';

interface TokenResponse { access_token: string; expires_in: number; }

/** Every browser-storage key this app owns. Sign-out removes only these. */
const keys = {
  accessToken: 'finops.access_token',
  accessTokenExpiry: 'finops.access_token_expiry',
  oauthState: 'finops.oauth_state',
  pkceVerifier: 'finops.pkce_verifier',
  mockObjectId: 'finops.mock_object_id',
} as const;

/**
 * Browser sign-in. This is convenience only: the API validates every token and authorizes every request.
 * Mock mode sends a Development-only header; Entra mode uses authorization code + PKCE.
 * See docs/decisions/0003-hardening-and-demo-data.md for why this is not yet MSAL.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly mockMode = authConfig.mockMode;
  readonly personas = authConfig.mockPersonas;
  readonly persona = signal<string>(
    sessionStorage.getItem(keys.mockObjectId) ?? authConfig.mockPersonas[0].objectId);
  readonly authenticated = signal<boolean>(authConfig.mockMode || this.hasUnexpiredToken());

  async completeRedirect(): Promise<void> {
    if (authConfig.mockMode) return;
    const params = new URLSearchParams(location.search);
    const code = params.get('code');
    if (!code) return;
    const state = sessionStorage.getItem(keys.oauthState);
    const verifier = sessionStorage.getItem(keys.pkceVerifier);
    // One-time values: remove before use so a replayed redirect cannot reuse them.
    sessionStorage.removeItem(keys.oauthState);
    sessionStorage.removeItem(keys.pkceVerifier);
    history.replaceState({}, document.title, location.pathname);
    if (!state || params.get('state') !== state) throw new Error('Invalid OAuth state.');
    if (!verifier) throw new Error('Missing PKCE verifier.');

    const body = new URLSearchParams({ client_id: authConfig.spaClientId, grant_type: 'authorization_code',
      code, redirect_uri: authConfig.redirectUri, scope: `openid profile ${authConfig.apiScope}`, code_verifier: verifier });
    const response = await fetch(`https://login.microsoftonline.com/${authConfig.tenantId}/oauth2/v2.0/token`,
      { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body });
    if (!response.ok) throw new Error('Entra token exchange failed.');
    const token = await response.json() as TokenResponse;
    sessionStorage.setItem(keys.accessToken, token.access_token);
    sessionStorage.setItem(keys.accessTokenExpiry, String(Date.now() + token.expires_in * 1000));
    this.authenticated.set(true);
  }

  async signIn(): Promise<void> {
    if (authConfig.mockMode) { this.authenticated.set(true); return; }
    const verifier = this.base64Url(crypto.getRandomValues(new Uint8Array(48)));
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
    const state = crypto.randomUUID();
    sessionStorage.setItem(keys.pkceVerifier, verifier);
    sessionStorage.setItem(keys.oauthState, state);
    const query = new URLSearchParams({ client_id: authConfig.spaClientId, response_type: 'code',
      redirect_uri: authConfig.redirectUri, response_mode: 'query', scope: `openid profile ${authConfig.apiScope}`,
      state, code_challenge: this.base64Url(new Uint8Array(digest)), code_challenge_method: 'S256' });
    location.assign(`https://login.microsoftonline.com/${authConfig.tenantId}/oauth2/v2.0/authorize?${query}`);
  }

  signOut(): void {
    this.clearSession();
    if (!authConfig.mockMode) {
      location.assign(`https://login.microsoftonline.com/${authConfig.tenantId}/oauth2/v2.0/logout`
        + `?post_logout_redirect_uri=${encodeURIComponent(authConfig.redirectUri)}`);
    }
  }

  /** Development only: switch the synthetic persona the mock header represents. */
  selectPersona(objectId: string): void {
    if (!authConfig.mockMode || !authConfig.mockPersonas.some((p) => p.objectId === objectId)) return;
    sessionStorage.setItem(keys.mockObjectId, objectId);
    this.persona.set(objectId);
  }

  /** Called when the API rejects the caller's credentials (401): the token is gone or expired. */
  handleUnauthorized(): void {
    if (authConfig.mockMode) return;
    sessionStorage.removeItem(keys.accessToken);
    sessionStorage.removeItem(keys.accessTokenExpiry);
    this.authenticated.set(false);
  }

  authorizationHeaders(): Record<string, string> {
    if (authConfig.mockMode) return { 'X-Development-User': this.persona() };
    const token = sessionStorage.getItem(keys.accessToken);
    return token && this.hasUnexpiredToken() ? { Authorization: `Bearer ${token}` } : {};
  }

  private hasUnexpiredToken(): boolean {
    return !!sessionStorage.getItem(keys.accessToken)
      && Number(sessionStorage.getItem(keys.accessTokenExpiry) ?? 0) > Date.now();
  }

  private clearSession(): void {
    Object.values(keys).forEach((key) => sessionStorage.removeItem(key));
    this.authenticated.set(authConfig.mockMode);
  }

  private base64Url(value: Uint8Array): string {
    return btoa(String.fromCharCode(...value)).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
  }
}
