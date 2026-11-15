import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { authConfig } from './auth.config';
import { authInterceptor } from './auth.interceptor';
import { applyRuntimeConfig, loadRuntimeConfig } from './runtime-config';

const defaults = { ...authConfig };
const entra = {
  apiBaseUrl: 'https://finops-api.example.azurecontainerapps.io', mockMode: false,
  tenantId: '11111111-2222-3333-4444-555555555555', spaClientId: '66666666-7777-8888-9999-000000000000',
  apiScope: 'api://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/FinOps.Access',
};

afterEach(() => Object.assign(authConfig, defaults));

describe('runtime config', () => {
  it('applies a deployment configuration', () => {
    applyRuntimeConfig(entra);
    expect(authConfig.apiBaseUrl).toBe(entra.apiBaseUrl);
    expect(authConfig.mockMode).toBe(false);
    expect(authConfig.spaClientId).toBe(entra.spaClientId);
  });

  it.each([
    ['http://insecure.example', 'https origin'],
    ['https://api.example/api', 'https origin'],
    ['https://api.example/', 'https origin'],
  ])('rejects the API base URL %s', (apiBaseUrl, message) => {
    expect(() => applyRuntimeConfig({ ...entra, apiBaseUrl })).toThrow(message);
    expect(authConfig.apiBaseUrl).toBe('');
  });

  it('refuses Entra mode without real identifiers', () => {
    expect(() => applyRuntimeConfig({ ...entra, tenantId: 'YOUR_ENTRA_TENANT_ID' })).toThrow('GUIDs');
    expect(() => applyRuntimeConfig({ ...entra, apiScope: 'User.Read' })).toThrow('FinOps.Access');
    expect(authConfig.mockMode).toBe(true);
  });

  it('keeps local defaults when config.json is absent, and fails loudly when it is broken', async () => {
    await loadRuntimeConfig(vi.fn().mockResolvedValue(new Response(null, { status: 404 })));
    expect(authConfig.mockMode).toBe(true);
    await expect(loadRuntimeConfig(vi.fn().mockResolvedValue(new Response('oops', { status: 500 }))))
      .rejects.toThrow('500');
  });
});

describe('authInterceptor with a separate API origin', () => {
  let http: HttpTestingController;
  let client: HttpClient;

  beforeEach(() => {
    sessionStorage.clear();
    applyRuntimeConfig({ apiBaseUrl: 'https://api.example', mockMode: true });
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
  });
  afterEach(() => http.verify());

  it('sends API calls to the API origin with credentials, and health checks without', () => {
    client.get('/api/me').subscribe();
    client.get('/health/ready').subscribe();
    expect(http.expectOne('https://api.example/api/me').request.headers.has('X-Development-User')).toBe(true);
    expect(http.expectOne('https://api.example/health/ready').request.headers.has('X-Development-User')).toBe(false);
  });

  it('leaves other URLs alone', () => {
    client.get('/config.json').subscribe();
    expect(http.expectOne('/config.json').request.headers.has('X-Development-User')).toBe(false);
  });
});
