import { ApplicationConfig, inject, provideAppInitializer } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

/**
 * Finishes an Entra sign-in redirect before the router reads the URL, then puts the page the user asked for into the
 * address bar so the first navigation lands there. The redirect URI stays the site root, so the Entra app
 * registration does not change.
 */
async function completeSignIn(auth: AuthService): Promise<void> {
  try {
    if (await auth.completeRedirect()) history.replaceState({}, document.title, auth.consumeReturnUrl());
  } catch (error: unknown) {
    console.error('Sign-in failed', error);
    auth.signInError.set('Sign-in did not complete. Please try again.');
  }
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideHttpClient(withInterceptors([authInterceptor])),
    provideRouter(routes,
      // Route params, query params, data and resolved values bind to component inputs of the same name.
      withComponentInputBinding(),
      // Children see the shell's resolved user; navigating to the current URL re-runs guards (after a persona switch).
      withRouterConfig({ paramsInheritanceStrategy: 'always', onSameUrlNavigation: 'reload' })),
    provideAppInitializer(() => completeSignIn(inject(AuthService))),
  ],
};
