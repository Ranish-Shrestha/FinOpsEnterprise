import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { authConfig } from './auth.config';
import { authInterceptor } from './auth.interceptor';
import { AuthService, safeReturnUrl } from './auth.service';

describe('AuthService (Development mock mode)', () => {
  beforeEach(() => sessionStorage.clear());

  it('defaults to the first synthetic persona', () => {
    const auth = TestBed.inject(AuthService);
    expect(auth.authorizationHeaders()).toEqual({ 'X-Development-User': authConfig.mockPersonas[0].objectId });
  });

  it('switches persona and remembers it for the browser session', () => {
    const manager = authConfig.mockPersonas[1].objectId;
    TestBed.inject(AuthService).selectPersona(manager);
    expect(sessionStorage.getItem('finops.mock_object_id')).toBe(manager);
    expect(TestBed.inject(AuthService).authorizationHeaders()['X-Development-User']).toBe(manager);
  });

  it('ignores object IDs that are not catalogued personas', () => {
    const auth = TestBed.inject(AuthService);
    auth.selectPersona('99999999-9999-9999-9999-999999999999');
    expect(auth.persona()).toBe(authConfig.mockPersonas[0].objectId);
  });

  it('starts signed out until a persona is chosen on the sign-in page', async () => {
    const auth = TestBed.inject(AuthService);
    expect(auth.authenticated()).toBe(false);
    await auth.signIn();
    expect(auth.authenticated()).toBe(true);
    auth.signOut();
    expect(auth.authenticated()).toBe(false);
  });

  it('remembers a same-origin return URL once, and falls back to the dashboard', async () => {
    const auth = TestBed.inject(AuthService);
    await auth.signIn('/expenses?view=queue');
    expect(auth.consumeReturnUrl()).toBe('/expenses?view=queue');
    expect(auth.consumeReturnUrl()).toBe('/dashboard');
    await auth.signIn('https://evil.example/');
    expect(auth.consumeReturnUrl()).toBe('/dashboard');
  });

  it('signs out by removing only keys owned by this app', () => {
    sessionStorage.setItem('finops.mock_object_id', authConfig.mockPersonas[2].objectId);
    sessionStorage.setItem('unrelated.key', 'keep');
    TestBed.inject(AuthService).signOut();
    expect(sessionStorage.getItem('finops.mock_object_id')).toBeNull();
    expect(sessionStorage.getItem('unrelated.key')).toBe('keep');
  });
});

describe('safeReturnUrl', () => {
  it('accepts only paths on this origin', () => {
    expect(safeReturnUrl('/budgets')).toBe('/budgets');
    expect(safeReturnUrl('/expenses/e1?view=all')).toBe('/expenses/e1?view=all');
    for (const unsafe of ['https://evil.example', '//evil.example', '/\\evil.example', 'javascript:alert(1)', 'budgets', '', null, undefined]) {
      expect(safeReturnUrl(unsafe)).toBeNull();
    }
  });
});

describe('authInterceptor', () => {
  let http: HttpTestingController;
  let client: HttpClient;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
  });
  afterEach(() => http.verify());

  it('adds identity headers to API calls', () => {
    client.get('/api/me').subscribe();
    expect(http.expectOne('/api/me').request.headers.has('X-Development-User')).toBe(true);
  });

  it('never sends identity headers to non-API URLs', () => {
    client.get('/health').subscribe();
    client.get('https://example.invalid/api/me').subscribe();
    expect(http.expectOne('/health').request.headers.has('X-Development-User')).toBe(false);
    expect(http.expectOne('https://example.invalid/api/me').request.headers.has('X-Development-User')).toBe(false);
  });
});
