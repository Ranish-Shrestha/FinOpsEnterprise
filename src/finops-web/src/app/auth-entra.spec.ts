import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { authConfig } from './auth.config';
import { AuthService } from './auth.service';

describe('AuthService (Entra mode: authorization code + PKCE)', () => {
  const tenantId = '7a1d6c3e-5b2f-4e8a-9c0d-1f2e3a4b5c6d';
  const spaClientId = '0b9f8e7d-6c5b-4a3f-9e2d-1c0b9a8f7e6d';
  const apiScope = 'api://5e4d3c2b-1a0f-4e9d-8c7b-6a5f4e3d2c1b/FinOps.Access';
  const original = { ...authConfig };
  let navigations: string[];

  function service(): AuthService {
    const auth = TestBed.inject(AuthService);
    vi.spyOn(auth as unknown as { navigate(url: string): void }, 'navigate')
      .mockImplementation((url: string) => { navigations.push(url); });
    return auth;
  }

  async function sha256Base64Url(value: string): Promise<string> {
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value)));
    return btoa(String.fromCharCode(...digest)).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
  }

  function tokenEndpoint(response: Response) {
    const fetchMock = vi.fn().mockResolvedValue(response);
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  function storeToken(token: string, expiresInMs: number): void {
    sessionStorage.setItem('finops.access_token', token);
    sessionStorage.setItem('finops.access_token_expiry', String(Date.now() + expiresInMs));
  }

  function startedSignIn(state: string, verifier: string, redirectQuery: string): void {
    sessionStorage.setItem('finops.oauth_state', state);
    sessionStorage.setItem('finops.pkce_verifier', verifier);
    history.replaceState({}, '', `/${redirectQuery}`);
  }

  beforeEach(() => {
    sessionStorage.clear();
    navigations = [];
    Object.assign(authConfig, { mockMode: false, tenantId, spaClientId, apiScope });
  });

  afterEach(() => {
    Object.assign(authConfig, original);
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    history.replaceState({}, '', '/');
  });

  it('starts unauthenticated and sends no credentials', () => {
    const auth = service();
    expect(auth.authenticated()).toBe(false);
    expect(auth.authorizationHeaders()).toEqual({});
  });

  it('redirects to the tenant authorize endpoint with an S256 challenge of the stored verifier', async () => {
    await service().signIn();

    const url = new URL(navigations[0]);
    expect(`${url.origin}${url.pathname}`).toBe(`https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/authorize`);
    const query = url.searchParams;
    expect(query.get('client_id')).toBe(spaClientId);
    expect(query.get('response_type')).toBe('code');
    expect(query.get('redirect_uri')).toBe(authConfig.redirectUri);
    expect(query.get('scope')).toBe(`openid profile ${apiScope}`);
    expect(query.get('code_challenge_method')).toBe('S256');
    expect(query.get('state')).toBe(sessionStorage.getItem('finops.oauth_state'));

    const verifier = sessionStorage.getItem('finops.pkce_verifier')!;
    expect(verifier).toMatch(/^[A-Za-z0-9_-]{43,128}$/); // RFC 7636 length and alphabet
    expect(query.get('code_challenge')).toBe(await sha256Base64Url(verifier));
    expect(query.has('code_verifier')).toBe(false); // only the challenge goes to the authorize endpoint
  });

  it('uses a fresh state and verifier on every sign-in', async () => {
    const auth = service();
    await auth.signIn();
    const state = sessionStorage.getItem('finops.oauth_state');
    const verifier = sessionStorage.getItem('finops.pkce_verifier');
    await auth.signIn();
    expect(sessionStorage.getItem('finops.oauth_state')).not.toBe(state);
    expect(sessionStorage.getItem('finops.pkce_verifier')).not.toBe(verifier);
  });

  it('exchanges the code with the verifier, stores the token and cleans the URL', async () => {
    startedSignIn('expected-state', 'the-verifier', '?code=auth-code&state=expected-state');
    const fetchMock = tokenEndpoint(new Response(JSON.stringify({ access_token: 'access-token', expires_in: 3600 })));

    const auth = service();
    await auth.completeRedirect();

    const [endpoint, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(endpoint).toBe(`https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/token`);
    const body = new URLSearchParams(init.body as URLSearchParams);
    const fields: Record<string, string> = {};
    body.forEach((value, key) => { fields[key] = value; });
    expect(fields).toEqual({
      client_id: spaClientId, grant_type: 'authorization_code', code: 'auth-code',
      redirect_uri: authConfig.redirectUri, scope: `openid profile ${apiScope}`, code_verifier: 'the-verifier',
    });
    expect(body.has('client_secret')).toBe(false); // public client: no secret in the browser
    expect(auth.authenticated()).toBe(true);
    expect(auth.authorizationHeaders()).toEqual({ Authorization: 'Bearer access-token' });
    expect(location.search).toBe(''); // the code is not left in the address bar or history
    expect(sessionStorage.getItem('finops.oauth_state')).toBeNull();
    expect(sessionStorage.getItem('finops.pkce_verifier')).toBeNull();
  });

  it('rejects a redirect whose state does not match, without calling Entra', async () => {
    startedSignIn('expected-state', 'the-verifier', '?code=auth-code&state=attacker-state');
    const fetchMock = tokenEndpoint(new Response('{}'));

    const auth = service();
    await expect(auth.completeRedirect()).rejects.toThrow('Invalid OAuth state.');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(auth.authenticated()).toBe(false);
    // One-time values are consumed even on failure, so the same redirect cannot be replayed.
    expect(sessionStorage.getItem('finops.oauth_state')).toBeNull();
    expect(sessionStorage.getItem('finops.pkce_verifier')).toBeNull();
  });

  it('rejects a redirect when no sign-in was started in this tab', async () => {
    history.replaceState({}, '', '/?code=auth-code&state=anything');
    const fetchMock = tokenEndpoint(new Response('{}'));
    await expect(service().completeRedirect()).rejects.toThrow('Invalid OAuth state.');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('stays signed out when the token exchange fails', async () => {
    startedSignIn('s', 'v', '?code=c&state=s');
    tokenEndpoint(new Response('{"error":"invalid_grant"}', { status: 400 }));

    const auth = service();
    await expect(auth.completeRedirect()).rejects.toThrow('Entra token exchange failed.');
    expect(auth.authenticated()).toBe(false);
    expect(sessionStorage.getItem('finops.access_token')).toBeNull();
  });

  it('does nothing on a normal page load without a code', async () => {
    const fetchMock = tokenEndpoint(new Response('{}'));
    await service().completeRedirect();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('does not send an expired token', () => {
    storeToken('old-token', -1000);
    const auth = service();
    expect(auth.authenticated()).toBe(false);
    expect(auth.authorizationHeaders()).toEqual({});
  });

  it('never sends the development header, even if a persona is stored', () => {
    sessionStorage.setItem('finops.mock_object_id', authConfig.mockPersonas[4].objectId);
    expect(service().authorizationHeaders()).not.toHaveProperty('X-Development-User');
  });

  it('drops the token when the API answers 401', () => {
    storeToken('token', 60_000);
    const auth = service();
    auth.handleUnauthorized();
    expect(auth.authenticated()).toBe(false);
    expect(sessionStorage.getItem('finops.access_token')).toBeNull();
  });

  it('signs out of Entra and returns to the app', () => {
    storeToken('token', 60_000);
    service().signOut();
    expect(sessionStorage.getItem('finops.access_token')).toBeNull();
    const url = new URL(navigations[0]);
    expect(`${url.origin}${url.pathname}`).toBe(`https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/logout`);
    expect(url.searchParams.get('post_logout_redirect_uri')).toBe(authConfig.redirectUri);
  });
});
