import { Routes, UrlMatchResult, UrlSegment } from '@angular/router';
import { pagePermissions } from './core/permissions';
import { authGuard, departmentsResolver, guestGuard, permissionGuard, userResolver } from './core/guards';
import { AuthLayout } from './layouts/auth-layout/auth-layout';
import { ShellLayout } from './layouts/shell-layout/shell-layout';

const title = (page: string) => `${page} · FinOps Enterprise`;

/** `/expenses` and `/expenses/:id` share one route, so opening an expense keeps the same component (and its list). */
export function expensesMatcher(segments: UrlSegment[]): UrlMatchResult | null {
  if (segments[0]?.path !== 'expenses' || segments.length > 2) return null;
  return { consumed: segments, posParams: segments[1] ? { id: segments[1] } : {} };
}

export const routes: Routes = [
  // Before the empty-path layouts: an empty-path parent would otherwise match "/" with no child page.
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  {
    path: '',
    component: AuthLayout,
    children: [
      { path: 'login', title: title('Sign in'), canActivate: [guestGuard], loadComponent: () => import('./pages/auth/login').then((m) => m.Login) },
      { path: 'signup', title: title('Request access'), canActivate: [guestGuard], loadComponent: () => import('./pages/auth/signup').then((m) => m.Signup) },
      { path: 'forgot-password', title: title('Reset your password'), loadComponent: () => import('./pages/auth/forgot-password').then((m) => m.ForgotPassword) },
      { path: 'mfa', title: title('Multi-factor authentication'), loadComponent: () => import('./pages/auth/mfa').then((m) => m.Mfa) },
      { path: 'access-denied', title: title('No access'), loadComponent: () => import('./pages/auth/access-denied').then((m) => m.AccessDenied) },
      { path: 'signed-out', title: title('Signed out'), loadComponent: () => import('./pages/auth/signed-out').then((m) => m.SignedOut) },
    ],
  },
  {
    path: '',
    component: ShellLayout,
    canActivate: [authGuard],
    resolve: { user: userResolver, departments: departmentsResolver },
    // Re-check access on every navigation, including the same-URL reload after a persona switch.
    runGuardsAndResolvers: 'always',
    children: [
      { path: 'dashboard', title: title('Overview'), loadComponent: () => import('./pages/dashboard/dashboard').then((m) => m.Dashboard) },
      { matcher: expensesMatcher, title: title('Expenses'), loadComponent: () => import('./expenses/expenses').then((m) => m.Expenses) },
      {
        path: 'budgets', title: title('Budgets'), canActivate: [permissionGuard(...pagePermissions.budgets)], runGuardsAndResolvers: 'always',
        loadComponent: () => import('./budgets/budgets').then((m) => m.Budgets),
      },
      {
        path: 'reports', title: title('Reports'), canActivate: [permissionGuard(...pagePermissions.reports)], runGuardsAndResolvers: 'always',
        loadComponent: () => import('./reports/reports').then((m) => m.Reports),
      },
      {
        path: 'notifications', title: title('Notifications'), data: { mode: 'notifications' },
        loadComponent: () => import('./activity/activity').then((m) => m.Activity),
      },
      {
        path: 'admin/outbox', title: title('Outbox'), data: { mode: 'outbox' },
        canActivate: [permissionGuard(...pagePermissions.outbox)], runGuardsAndResolvers: 'always',
        loadComponent: () => import('./activity/activity').then((m) => m.Activity),
      },
    ],
  },
  {
    path: '',
    component: AuthLayout,
    children: [{ path: '**', title: title('Page not found'), loadComponent: () => import('./pages/not-found').then((m) => m.NotFound) }],
  },
];
