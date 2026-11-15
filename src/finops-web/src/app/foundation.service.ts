import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { catchError, forkJoin, map, of, timeout } from 'rxjs';

export interface Department { id: string; name: string; }
export interface CurrentUser { displayName: string; email: string; roles: string[]; permissions: string[]; departmentIds: string[]; }
/** How the caller signed in, from `GET /api/me/sign-in` (the API reads its validated token). */
export interface SignInDetails { identityProvider: string; methods: string[]; multiFactor: boolean | null; }
export interface FoundationState {
  apiOnline: boolean;
  databaseReady: boolean;
  departments: Department[] | null;
  user: CurrentUser | null;
  signIn: SignInDetails | null;
}

@Injectable({ providedIn: 'root' })
export class FoundationService {
  private readonly http = inject(HttpClient);

  load() {
    const health = (url: string) => this.http.get<{ status: string }>(url).pipe(
      timeout(6000),
      map((response) => response.status === 'Healthy'),
      catchError(() => of(false)),
    );
    return forkJoin({
      apiOnline: health('/health'),
      databaseReady: health('/health/ready'),
      departments: this.http.get<Department[]>('/api/departments').pipe(
        timeout(6000), catchError(() => of(null)),
      ),
      user: this.http.get<CurrentUser>('/api/me').pipe(timeout(6000), catchError(() => of(null))),
      signIn: this.http.get<SignInDetails>('/api/me/sign-in').pipe(timeout(6000), catchError(() => of(null))),
    });
  }
}
