import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { authConfig } from './auth.config';
import { AuthService } from './auth.service';

const isApi = (url: string) => url.startsWith('/api/');
const isHealth = (url: string) => url === '/health' || url.startsWith('/health/');

/**
 * Routes the app's relative API and health calls to the configured API origin (same origin when empty), adds
 * credentials to API calls only, and drops an expired session on 401. Absolute URLs pass through untouched and never
 * receive credentials.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  if (isHealth(request.url)) return next(request.clone({ url: authConfig.apiBaseUrl + request.url }));
  if (!isApi(request.url)) return next(request);

  const auth = inject(AuthService);
  return next(request.clone({ url: authConfig.apiBaseUrl + request.url, setHeaders: auth.authorizationHeaders() })).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401) auth.handleUnauthorized();
      return throwError(() => error);
    }),
  );
};
