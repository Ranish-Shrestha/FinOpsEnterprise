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
  oidcNonce: 'finops.oidc_nonce',
  provider: 'finops.provider',
} as const;

/** Who signed the user in. Microsoft is Entra (authorization code + PKCE); Google is a direct OIDC sign-in. */
export type SignInProvider = 'microsoft' | 'google';

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
 * Mock mode sends a Development-only header; Entra uses authorization code + PKCE; Google (optional) returns an ID
 * token directly to the browser, checked against a one-time state and nonce, and the API validates it.
 * See docs/decisions/0003-hardening-and-demo-data.md for why this is not yet MSAL.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly mockMode = authConfig.mockMode;
  /** Which real sign-in buttons the login page offers. */
  readonly entraEnabled = !authConfig.mockMode && authConfig.entraEnabled;
  readonly googleEnabled = !authConfig.mockMode && !!authConfig.googleClientId;
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
    if (/^#(.*&)?(id_token|error)=/.test(location.hash)) return this.completeGoogleRedirect();
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
    sessionStorage.setItem(keys.provider, 'microsoft');
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

  async signIn(returnUrl?: string | null, provider: SignInProvider = 'microsoft'): Promise<void> {
    const target = safeReturnUrl(returnUrl);
    if (target) sessionStorage.setItem(keys.returnUrl, target);
    else sessionStorage.removeItem(keys.returnUrl);
    this.signInError.set(null);
    if (authConfig.mockMode) {
      sessionStorage.setItem(keys.mockSignedIn, 'true');
      this.authenticated.set(true);
      return;
    }
    if (provider === 'google') {
      this.startGoogleSignIn();
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

  /**
   * Clears this app's session. After an Entra sign-in it also leaves for the Entra logout page and returns true;
   * otherwise (mock, Google) it returns false and the caller routes. Google has no sign-out for other sites: the
   * browser stays signed in to Google, but this app's token is gone.
   */
  signOut(): boolean {
    const provider = sessionStorage.getItem(keys.provider);
    this.clearSession();
    if (authConfig.mockMode || provider === 'google') return false;
    this.navigate(`https://login.microsoftonline.com/${authConfig.tenantId}/oauth2/v2.0/logout`
      + `?post_logout_redirect_uri=${encodeURIComponent(authConfig.redirectUri)}`);
    return true;
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
    const oid = this.payload(sessionStorage.getItem(keys.accessToken))?.['oid'];
    return typeof oid === 'string' ? oid : null;
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

  /** Google's OIDC endpoint, implicit `id_token` response: a public client gets an ID token without a secret. */
  private startGoogleSignIn(): void {
    const state = crypto.randomUUID();
    const nonce = this.base64Url(crypto.getRandomValues(new Uint8Array(32)));
    sessionStorage.setItem(keys.oauthState, state);
    sessionStorage.setItem(keys.oidcNonce, nonce);
    const query = new URLSearchParams({ client_id: authConfig.googleClientId, response_type: 'id_token',
      redirect_uri: authConfig.redirectUri, scope: 'openid email profile', state, nonce, prompt: 'select_account' });
    this.navigate(`https://accounts.google.com/o/oauth2/v2/auth?${query}`);
  }

  /**
   * Finishes a Google redirect (`#id_token=…&state=…`). The state and nonce must match the ones saved when sign-in
   * started, so a token injected into the URL is refused. The signature is not checked here: the API verifies it.
   */
  private completeGoogleRedirect(): boolean {
    const params = new URLSearchParams(location.hash.slice(1));
    const state = sessionStorage.getItem(keys.oauthState);
    const nonce = sessionStorage.getItem(keys.oidcNonce);
    sessionStorage.removeItem(keys.oauthState);
    sessionStorage.removeItem(keys.oidcNonce);
    history.replaceState({}, document.title, location.pathname);
    if (!state || params.get('state') !== state) throw new Error('Invalid OAuth state.');
    const error = params.get('error');
    if (error) throw new Error(error === 'access_denied' ? 'Google sign-in was cancelled.' : 'Google sign-in failed.');
    const idToken = params.get('id_token');
    const claims = this.payload(idToken);
    if (!idToken || !nonce || claims?.['nonce'] !== nonce) throw new Error('Invalid Google ID token.');
    const exp = claims['exp'];
    if (typeof exp !== 'number') throw new Error('Invalid Google ID token.');
    sessionStorage.setItem(keys.accessToken, idToken);
    sessionStorage.setItem(keys.accessTokenExpiry, String(exp * 1000));
    sessionStorage.setItem(keys.provider, 'google');
    this.authenticated.set(true);
    this.signInError.set(null);
    return true;
  }

  /** A JWT's payload, unverified (display and nonce checks only), or null. */
  private payload(token: string | null | undefined): Record<string, unknown> | null {
    try {
      const part = token?.split('.')[1];
      if (!part) return null;
      const json = atob(part.replaceAll('-', '+').replaceAll('_', '/'));
      const value: unknown = JSON.parse(json);
      return typeof value === 'object' && value !== null ? value as Record<string, unknown> : null;
    } catch {
      return null;
    }
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
