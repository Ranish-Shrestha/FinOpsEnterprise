import { CurrencyPipe, DatePipe, DecimalPipe, UpperCasePipe } from '@angular/common';
import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { catchError, firstValueFrom, forkJoin, map, Observable, of } from 'rxjs';
import { ActivityService, AppNotification } from '../../activity/activity.service';
import { BudgetsService, BudgetVsActualRow } from '../../budgets/budgets.service';
import { SessionStore } from '../../core/session.store';
import { ExpensesService, ExpenseStatus, ExpenseSummary, ListFilter } from '../../expenses/expenses.service';

/** What the dashboard shows. Each part loads on its own: null means "not available to you" or "failed". */
interface DashboardData {
  drafts: number | null;
  awaitingApproval: number | null;
  recent: ExpenseSummary[] | null;
  toApprove: { count: number; items: ExpenseSummary[] } | null;
  toPost: number | null;
  inbox: { unread: number; items: AppNotification[] } | null;
  budget: BudgetVsActualRow[] | null;
}

const monthNames = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October',
  'November', 'December'];

/**
 * The caller's work at a glance, by role: their own expenses, what waits for them (approvals, posting), unread
 * notifications and this month's budget. Everything comes from the same API endpoints as the feature pages, so the
 * API's authorization decides what appears; the permission checks here only skip calls that would answer 403.
 */
@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, CurrencyPipe, DatePipe, DecimalPipe, UpperCasePipe],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.css',
})
export class Dashboard {
  private readonly expenses = inject(ExpensesService);
  private readonly budgets = inject(BudgetsService);
  private readonly activity = inject(ActivityService);
  readonly session = inject(SessionStore);
  readonly state = this.session.state;

  readonly data = signal<DashboardData | null>(null);
  readonly loading = signal(false);
  readonly now = new Date();
  readonly monthLabel = `${monthNames[this.now.getUTCMonth()]} ${this.now.getUTCFullYear()}`;

  readonly firstName = computed(() => this.session.user()?.displayName.split(' ')[0] ?? '');
  readonly greeting = (() => {
    const hour = this.now.getHours();
    return hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening';
  })();
  readonly canCreate = computed(() => this.session.hasPermission('expenses.create'));
  readonly canApprove = computed(() => this.session.hasPermission('expenses.approve'));
  readonly canPost = computed(() => this.session.hasPermission('expenses.post'));
  readonly canSeeBudgets = computed(() => this.session.hasPermission('budgets.read.department', 'budgets.read.all'));
  readonly departmentNames = computed(() => (this.session.departments() ?? []).map((d) => d.name));

  readonly budgetTotals = computed(() => {
    const rows = this.data()?.budget ?? [];
    const budget = rows.reduce((sum, r) => sum + (r.budget ?? 0), 0);
    const spent = rows.reduce((sum, r) => sum + r.posted + r.committed, 0);
    return { budget, spent, percent: budget > 0 ? Math.round((spent / budget) * 100) : null };
  });

  constructor() {
    // Reload whenever the signed-in user changes (sign-in, persona switch, refresh).
    effect(() => {
      if (this.session.user()) untracked(() => void this.load());
    });
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.data.set(await firstValueFrom(forkJoin({
        drafts: this.count({ mine: true, status: 'Draft' }),
        awaitingApproval: this.count({ mine: true, status: 'Submitted' }),
        recent: this.page({ mine: true, pageSize: 5 }).pipe(map((p) => p?.items ?? null)),
        toApprove: this.canApprove()
          ? this.page({ status: 'Submitted', sort: 'submittedAt', pageSize: 5 })
            .pipe(map((p) => p && { count: p.totalCount, items: p.items }))
          : of(null),
        toPost: this.canPost() ? this.count({ status: 'Approved' }) : of(null),
        inbox: this.activity.inbox().pipe(
          map((i) => ({ unread: i.unreadCount, items: i.items.filter((n) => !n.readAtUtc).slice(0, 4) })),
          catchError(() => of(null))),
        budget: this.canSeeBudgets()
          ? this.budgets.report(this.now.getUTCFullYear(), this.now.getUTCMonth() + 1).pipe(catchError(() => of(null)))
          : of(null),
      })));
    } finally {
      this.loading.set(false);
    }
  }

  refresh(): void {
    if (this.loading() || this.session.loading()) return;
    void this.session.refresh(); // the effect reloads the dashboard when the user is re-read
  }

  statusClass(status: ExpenseStatus): string {
    return `status status-${status.toLowerCase()}`;
  }

  notificationText(n: AppNotification): string {
    return n.kind === 'ExpenseApproved' ? 'Approved' : n.kind === 'ExpenseRejected' ? 'Rejected' : 'Posted';
  }

  meterClass(percent: number | null): string {
    if (percent === null) return 'meter-fill';
    return percent > 100 ? 'meter-fill over' : percent >= 85 ? 'meter-fill warn' : 'meter-fill';
  }

  private page(filter: ListFilter) {
    return this.expenses.list(filter).pipe(catchError(() => of(null)));
  }

  private count(filter: ListFilter): Observable<number | null> {
    return this.page({ ...filter, pageSize: 1 }).pipe(map((p) => p?.totalCount ?? null));
  }
}
