import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, withComponentInputBinding, withRouterConfig } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { routes } from '../app.routes';
import { CurrentUser, FoundationService, FoundationState } from '../foundation.service';

const employee: CurrentUser = { displayName: 'Demo Employee', email: 'e@example.invalid', roles: ['Employee'],
  permissions: ['expenses.create'], departmentIds: ['d1'] };
const admin: CurrentUser = { ...employee, displayName: 'Demo Administrator', roles: ['Administrator'],
  permissions: ['messaging.manage', 'budgets.read.all', 'reports.read.all'] };

describe('routing guards (mock mode)', () => {
  let state: FoundationState;

  /** Starts the app's real routes; returns a function that navigates and reports where the router ended up. */
  async function start(signedIn: boolean): Promise<(url: string) => Promise<string>> {
    if (signedIn) sessionStorage.setItem('finops.mock_signed_in', 'true');
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding(), withRouterConfig({ paramsInheritanceStrategy: 'always', onSameUrlNavigation: 'reload' })),
        provideHttpClient(), provideHttpClientTesting(),
        { provide: FoundationService, useValue: { load: () => of(state) } },
      ],
    });
    const harness = await RouterTestingHarness.create();
    const router = TestBed.inject(Router);
    return async (url) => { await harness.navigateByUrl(url); return router.url; };
  }

  async function navigate(url: string, signedIn: boolean): Promise<string> {
    return (await start(signedIn))(url);
  }

  beforeEach(() => {
    sessionStorage.clear();
    state = { apiOnline: true, databaseReady: true, departments: [{ id: 'd1', name: 'Demo Engineering' }], user: employee, signIn: null };
  });

  it('sends a signed-out visitor to sign in, remembering the page they asked for', async () => {
    expect(await navigate('/expenses?view=queue', false)).toBe('/login?returnUrl=%2Fexpenses%3Fview%3Dqueue');
  });

  it('opens the workspace at the dashboard', async () => {
    expect(await navigate('/', true)).toBe('/dashboard');
  });

  it('keeps a signed-in caller away from the sign-in page', async () => {
    expect(await navigate('/login', true)).toBe('/dashboard');
  });

  it('redirects pages the caller has no permission for back to the dashboard', async () => {
    const go = await start(true);
    expect(await go('/budgets')).toBe('/dashboard');
    expect(await go('/admin/outbox')).toBe('/dashboard');
  });

  it('lets a caller with the permission in', async () => {
    state = { ...state, user: admin };
    expect(await navigate('/admin/outbox', true)).toBe('/admin/outbox');
  });

  it('sends a signed-in account without a FinOps profile to access denied', async () => {
    state = { ...state, user: null };
    expect(await navigate('/expenses', true)).toBe('/access-denied');
  });

  it('shows the account-help pages to anyone', async () => {
    const go = await start(false);
    expect(await go('/forgot-password')).toBe('/forgot-password');
    expect(await go('/mfa')).toBe('/mfa');
    expect(await go('/signup')).toBe('/signup');
  });

  it('answers unknown addresses with the not-found page', async () => {
    expect(await navigate('/no-such-page', false)).toBe('/no-such-page');
  });
});
