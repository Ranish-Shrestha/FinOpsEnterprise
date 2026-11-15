import { inject } from '@angular/core';
import { CanActivateFn, ResolveFn, Router } from '@angular/router';
import { AuthService } from '../auth.service';
import { CurrentUser, Department } from '../foundation.service';
import { SessionStore } from './session.store';

/**
 * Navigation guards. They decide which page to show, not what the caller may do: the API checks every request again
 * and answers 403 regardless of what the browser allowed.
 */

/** Signed-in and provisioned callers only. Otherwise go to /login (keeping where they wanted to go) or /access-denied. */
export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const session = inject(SessionStore);
  if (!auth.authenticated()) return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  await session.ensureLoaded();
  // No profile while the API is up means 403 from /api/me: not provisioned, or inactive. If the API is down the
  // dashboard explains that instead.
  if (!session.user() && session.state()?.apiOnline) return router.createUrlTree(['/access-denied']);
  return true;
};

/** Sign-in pages: a caller who is already signed in goes straight to the workspace. */
export const guestGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.authenticated() ? inject(Router).createUrlTree(['/dashboard']) : true;
};

/** Pages that need one of the permissions. Without it, the caller goes back to the dashboard. */
export function permissionGuard(...anyOf: string[]): CanActivateFn {
  return async () => {
    // inject() only works before the first await.
    const session = inject(SessionStore);
    const router = inject(Router);
    await session.ensureLoaded();
    return session.hasPermission(...anyOf) ? true : router.createUrlTree(['/dashboard']);
  };
}

/** Bound to the feature components' `user` input. */
export const userResolver: ResolveFn<CurrentUser | null> = async () => {
  const session = inject(SessionStore);
  await session.ensureLoaded();
  return session.user();
};

/** Bound to the feature components' `departments` input. */
export const departmentsResolver: ResolveFn<Department[] | null> = async () => {
  const session = inject(SessionStore);
  await session.ensureLoaded();
  return session.departments();
};
