import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { pagePermissions } from '../../core/permissions';
import { AuthService } from '../../auth.service';
import { SessionStore } from '../../core/session.store';

interface NavItem { path: string; label: string; anyOf?: readonly string[]; }

const navItems: readonly NavItem[] = [
  { path: '/dashboard', label: 'Overview' },
  { path: '/expenses', label: 'Expenses' },
  { path: '/budgets', label: 'Budgets', anyOf: pagePermissions.budgets },
  { path: '/reports', label: 'Reports', anyOf: pagePermissions.reports },
  { path: '/notifications', label: 'Notifications' },
  { path: '/admin/outbox', label: 'Outbox', anyOf: pagePermissions.outbox },
];

/** The signed-in workspace: sidebar navigation, a top bar with the user menu, and the current page. */
@Component({
  selector: 'app-shell-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './shell-layout.html',
  styleUrl: './shell-layout.css',
})
export class ShellLayout {
  private readonly router = inject(Router);
  readonly auth = inject(AuthService);
  readonly session = inject(SessionStore);
  readonly mockMode = this.auth.mockMode;

  /** Links the caller can use; the same permissions as the route guards. */
  readonly nav = computed(() => {
    this.session.user();
    return navItems.filter((item) => !item.anyOf || this.session.hasPermission(...item.anyOf));
  });

  /** "Expenses" from the route title "Expenses · FinOps Enterprise". */
  readonly pageName = toSignal(
    this.router.events.pipe(filter((e) => e instanceof NavigationEnd), map(() => this.currentPageName())),
    { initialValue: this.currentPageName() });

  async choosePersona(objectId: string): Promise<void> {
    this.auth.selectPersona(objectId);
    await this.session.refresh();
    // Re-run guards and resolvers: the new persona may not be allowed on this page.
    await this.router.navigateByUrl(this.router.url);
  }

  signOut(): void {
    this.auth.signOut();
    this.session.clear();
    if (this.mockMode) void this.router.navigateByUrl('/signed-out');
  }

  focusMain(event: Event): void {
    event.preventDefault();
    document.getElementById('main')?.focus();
  }

  private currentPageName(): string {
    let route = this.router.routerState.snapshot.root;
    while (route.firstChild) route = route.firstChild;
    return (route.title ?? '').split(' · ')[0];
  }
}
