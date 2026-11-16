import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { authConfig } from './auth.config';
import { AuthService } from './auth.service';

describe('AuthService (direct Google sign-in: OIDC id_token)', () => {
  const googleClientId = '123-abc.apps.googleusercontent.com';
  const original = { ...authConfig };
  let navigations: string[];

  function service(): AuthService {
    const auth = TestBed.inject(AuthService);
    vi.spyOn(auth as unknown as { navigate(url: string): void }, 'navigate')
      .mockImplementation((url: string) => { navigations.push(url); });
    return auth;
  }

  /** An unsigned JWT-shaped token. The browser never checks signatures; the API does. */
  function idToken(claims: Record<string, unknown>): string {
    const encode = (value: object) => btoa(JSON.stringify(value)).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
    return `${encode({ alg: 'RS256' })}.${encode(claims)}.signature`;
  }

  function returnFromGoogle(fragment: Record<string, string>): void {
    history.replaceState({}, '', `/#${new URLSearchParams(fragment)}`);
  }

  async function startSignIn(): Promise<{ auth: AuthService; state: string; nonce: string }> {
    const auth = service();
    await auth.signIn('/budgets', 'google');
    const query = new URL(navigations[0]).searchParams;
    return { auth, state: query.get('state')!, nonce: query.get('nonce')! };
  }

  beforeEach(() => {
    sessionStorage.clear();
    navigations = [];
    Object.assign(authConfig, { mockMode: false, entraEnabled: false, googleClientId });
  });

  afterEach(() => {
    Object.assign(authConfig, original);
    vi.restoreAllMocks();
    history.replaceState({}, '', '/');
  });

  it('offers only the configured provider', () => {
    const auth = service();
    expect(auth.googleEnabled).toBe(true);
    expect(auth.entraEnabled).toBe(false);
  });

  it('redirects to Google for an ID token with a one-time state and nonce', async () => {
    const { state, nonce } = await startSignIn();
    const url = new URL(navigations[0]);
    expect(`${url.origin}${url.pathname}`).toBe('https://accounts.google.com/o/oauth2/v2/auth');
    expect(url.searchParams.get('client_id')).toBe(googleClientId);
    expect(url.searchParams.get('response_type')).toBe('id_token');
    expect(url.searchParams.get('redirect_uri')).toBe(authConfig.redirectUri);
    expect(url.searchParams.get('scope')).toBe('openid email profile');
    expect(state).toBe(sessionStorage.getItem('finops.oauth_state'));
    expect(nonce).toBe(sessionStorage.getItem('finops.oidc_nonce'));
    expect(nonce.length).toBeGreaterThanOrEqual(43);
  });

  it('accepts the returned token, sends it to the API and clears the fragment', async () => {
    const { auth, state, nonce } = await startSignIn();
    const exp = Math.floor(Date.now() / 1000) + 3600;
    const token = idToken({ iss: 'https://accounts.google.com', sub: '42', nonce, exp });
    returnFromGoogle({ id_token: token, state });

    expect(await auth.completeRedirect()).toBe(true);
    expect(auth.authenticated()).toBe(true);
    expect(auth.authorizationHeaders()).toEqual({ Authorization: `Bearer ${token}` });
    expect(location.hash).toBe('');
    expect(auth.consumeReturnUrl()).toBe('/budgets');
    expect(sessionStorage.getItem('finops.oauth_state')).toBeNull();
    expect(sessionStorage.getItem('finops.oidc_nonce')).toBeNull();
  });

  it.each([
    ['a different state', (state: string, nonce: string) => ({ id_token: idToken({ nonce, exp: 9e9 }), state: `${state}x` }), 'state'],
    ['a different nonce (an injected token)', (state: string) => ({ id_token: idToken({ nonce: 'other', exp: 9e9 }), state }), 'ID token'],
    ['no expiry', (state: string, nonce: string) => ({ id_token: idToken({ nonce }), state }), 'ID token'],
    ['a malformed token', (state: string) => ({ id_token: 'not-a-jwt', state }), 'ID token'],
    ['the user cancelling', (state: string) => ({ error: 'access_denied', state }), 'cancelled'],
  ])('refuses a redirect with %s', async (_, fragment, message) => {
    const { auth, state, nonce } = await startSignIn();
    returnFromGoogle(fragment(state, nonce));
    await expect(auth.completeRedirect()).rejects.toThrow(message);
    expect(auth.authenticated()).toBe(false);
    expect(sessionStorage.getItem('finops.access_token')).toBeNull();
  });

  it('cannot replay a redirect: the state and nonce are single use', async () => {
    const { auth, state, nonce } = await startSignIn();
    const fragment = { id_token: idToken({ nonce, exp: Math.floor(Date.now() / 1000) + 3600 }), state };
    returnFromGoogle(fragment);
    await auth.completeRedirect();
    auth.signOut();
    returnFromGoogle(fragment);
    await expect(auth.completeRedirect()).rejects.toThrow('state');
  });

  it('signs out locally and lets the caller route, since Google has no app sign-out', async () => {
    const { auth, state, nonce } = await startSignIn();
    returnFromGoogle({ id_token: idToken({ nonce, exp: Math.floor(Date.now() / 1000) + 3600 }), state });
    await auth.completeRedirect();
    navigations = [];

    expect(auth.signOut()).toBe(false);
    expect(navigations).toEqual([]);
    expect(auth.authenticated()).toBe(false);
    expect(sessionStorage.getItem('finops.access_token')).toBeNull();
  });
});
