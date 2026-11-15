import { computed, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FoundationService, FoundationState } from '../foundation.service';

/**
 * The signed-in caller's access and the system status, loaded once and shared by the layout, guards and pages.
 * The UI uses it only to choose what to show: the API authorizes every request again.
 */
@Injectable({ providedIn: 'root' })
export class SessionStore {
  private readonly foundation = inject(FoundationService);
  private pending: Promise<void> | null = null;

  readonly state = signal<FoundationState | null>(null);
  readonly loading = signal(false);
  readonly user = computed(() => this.state()?.user ?? null);
  readonly departments = computed(() => this.state()?.departments ?? null);

  /** Loads the session unless it is already loaded. */
  ensureLoaded(): Promise<void> {
    return this.state() ? Promise.resolve() : this.refresh();
  }

  /** Reloads health, `/api/me` and departments. Concurrent callers share one request. */
  refresh(): Promise<void> {
    if (this.pending) return this.pending;
    this.loading.set(true);
    this.pending = firstValueFrom(this.foundation.load())
      .then((state) => this.state.set(state))
      .finally(() => { this.loading.set(false); this.pending = null; });
    return this.pending;
  }

  /** Forgets the session, for sign-out. */
  clear(): void {
    this.state.set(null);
  }

  /** True when the caller holds at least one of the permissions. */
  hasPermission(...anyOf: string[]): boolean {
    const held = this.user()?.permissions ?? [];
    return anyOf.some((permission) => held.includes(permission));
  }
}
