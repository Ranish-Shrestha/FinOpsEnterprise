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
  mockSignedIn: 'finops.mock_signed_in',
  returnUrl: 'finops.return_url',
} as const;

const defaultReturnUrl = '/dashboard';

/**
 * A same-origin path such as `/expenses?view=queue`, or null. Rejects absolute URLs, protocol-relative `//host` and
 * `/\host` (which browsers treat as `//host`), so a crafted `?returnUrl=` cannot send the user to another site.
 */
export function safeReturnUrl(value: string | null | undefined): string | null {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) return null;
  return value;
}

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
  readonly authenticated = signal<boolean>(this.hasSession());
  /** Why the last sign-in failed, for the login page. */
  readonly signInError = signal<string | null>(null);

  /**
   * Finishes an Entra redirect (`?code=&state=`) if the URL carries one. Returns true when a token was obtained.
   * Runs as an app initializer, before the router reads the URL.
   */
  async completeRedirect(): Promise<boolean> {
    if (authConfig.mockMode) return false;
    const params = new URLSearchParams(location.search);
    const code = params.get('code');
    if (!code) return false;
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
    this.signInError.set(null);
    return true;
  }

  /** Where to go after sign-in: the saved same-origin path, or the dashboard. Reading it removes it. */
  consumeReturnUrl(): string {
    const saved = safeReturnUrl(sessionStorage.getItem(keys.returnUrl));
    sessionStorage.removeItem(keys.returnUrl);
    return saved ?? defaultReturnUrl;
  }

  async signIn(returnUrl?: string | null): Promise<void> {
    const target = safeReturnUrl(returnUrl);
    if (target) sessionStorage.setItem(keys.returnUrl, target);
    else sessionStorage.removeItem(keys.returnUrl);
    this.signInError.set(null);
    if (authConfig.mockMode) {
      sessionStorage.setItem(keys.mockSignedIn, 'true');
      this.authenticated.set(true);
      return;
    }
    const verifier = this.base64Url(crypto.getRandomValues(new Uint8Array(48)));
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
    const state = crypto.randomUUID();
    sessionStorage.setItem(keys.pkceVerifier, verifier);
    sessionStorage.setItem(keys.oauthState, state);
    const query = new URLSearchParams({ client_id: authConfig.spaClientId, response_type: 'code',
      redirect_uri: authConfig.redirectUri, response_mode: 'query', scope: `openid profile ${authConfig.apiScope}`,
      state, code_challenge: this.base64Url(new Uint8Array(digest)), code_challenge_method: 'S256' });
    this.navigate(`https://login.microsoftonline.com/${authConfig.tenantId}/oauth2/v2.0/authorize?${query}`);
  }

  /** Clears this app's session. In Entra mode it also leaves for the Entra logout page; the caller routes otherwise. */
  signOut(): void {
    this.clearSession();
    if (!authConfig.mockMode) {
      this.navigate(`https://login.microsoftonline.com/${authConfig.tenantId}/oauth2/v2.0/logout`
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

  /**
   * The signed-in account's Entra object ID (`oid`), for the access-request page. Display only: the token is read
   * without verification, and the API validates it on every request.
   */
  accountObjectId(): string | null {
    if (authConfig.mockMode) return this.authenticated() ? this.persona() : null;
    const token = sessionStorage.getItem(keys.accessToken);
    try {
      const payload = token?.split('.')[1];
      if (!payload) return null;
      const json = atob(payload.replaceAll('-', '+').replaceAll('_', '/'));
      const oid = (JSON.parse(json) as { oid?: unknown }).oid;
      return typeof oid === 'string' ? oid : null;
    } catch {
      return null;
    }
  }

  authorizationHeaders(): Record<string, string> {
    if (authConfig.mockMode) return { 'X-Development-User': this.persona() };
    const token = sessionStorage.getItem(keys.accessToken);
    return token && this.hasUnexpiredToken() ? { Authorization: `Bearer ${token}` } : {};
  }

  /** Leaves the app for Entra. A method so tests can observe the destination instead of navigating. */
  protected navigate(url: string): void {
    location.assign(url);
  }

  private hasSession(): boolean {
    return authConfig.mockMode ? sessionStorage.getItem(keys.mockSignedIn) === 'true' : this.hasUnexpiredToken();
  }

  private hasUnexpiredToken(): boolean {
    return !!sessionStorage.getItem(keys.accessToken)
      && Number(sessionStorage.getItem(keys.accessTokenExpiry) ?? 0) > Date.now();
  }

  private clearSession(): void {
    Object.values(keys).forEach((key) => sessionStorage.removeItem(key));
    this.authenticated.set(false);
  }

  private base64Url(value: Uint8Array): string {
    return btoa(String.fromCharCode(...value)).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
  }
}
