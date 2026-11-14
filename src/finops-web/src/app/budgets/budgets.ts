import { CurrencyPipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { AuthService } from '../auth.service';
import { problemMessage } from '../expenses/expenses.service';
import { CurrentUser, Department } from '../foundation.service';
import { Budget, BudgetsService, BudgetVsActualRow } from './budgets.service';

const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/**
 * Budget vs. actual. The server decides which departments appear (managers: their own; finance and
 * administrators: all). Finance can set or change a month's budget.
 */
@Component({
  selector: 'app-budgets',
  imports: [CurrencyPipe, DecimalPipe],
  templateUrl: './budgets.html',
  styleUrl: './budgets.css',
})
export class Budgets {
  private readonly api = inject(BudgetsService);
  private readonly auth = inject(AuthService);

  readonly user = input<CurrentUser | null>(null);
  readonly departments = input<Department[] | null>(null);

  readonly monthNames = monthNames;
  readonly currentYear = new Date().getUTCFullYear();
  readonly years = [this.currentYear, this.currentYear - 1];
  readonly year = signal(this.currentYear);
  readonly month = signal<number | null>(new Date().getUTCMonth() + 1);

  readonly rows = signal<BudgetVsActualRow[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly formOpen = signal(false);
  readonly busy = signal(false);
  readonly formError = signal<string | null>(null);
  readonly form = signal({ departmentId: '', month: new Date().getUTCMonth() + 1, amount: 0 });

  readonly canRead = computed(() => this.has('budgets.read.all') || this.has('budgets.read.department'));
  readonly canManage = computed(() => this.has('budgets.manage'));
  readonly totals = computed(() => this.rows().reduce(
    (t, r) => ({ budget: t.budget + (r.budget ?? 0), posted: t.posted + r.posted, committed: t.committed + r.committed,
      pending: t.pending + r.pending }),
    { budget: 0, posted: 0, committed: 0, pending: 0 }));

  constructor() {
    effect(() => {
      this.auth.persona();
      this.user();
      untracked(() => { this.formOpen.set(false); if (this.canRead()) this.load(); else this.rows.set([]); });
    });
  }

  monthName(month: number): string {
    return monthNames[month - 1] ?? String(month);
  }

  /** Width of the utilization bar, capped so an overspend still fits the cell. */
  barWidth(row: BudgetVsActualRow): number {
    return Math.min(100, Math.max(0, row.utilizationPercent ?? 0));
  }

  barClass(row: BudgetVsActualRow): string {
    const used = row.utilizationPercent ?? 0;
    return used > 100 ? 'bar over' : used >= 80 ? 'bar warn' : 'bar';
  }

  setYear(value: string): void { this.year.set(Number(value)); this.load(); }
  setMonth(value: string): void { this.month.set(value ? Number(value) : null); this.load(); }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.report(this.year(), this.month() ?? undefined).subscribe({
      next: (rows) => { this.rows.set(rows); this.loading.set(false); },
      error: (e: unknown) => { this.rows.set([]); this.error.set(problemMessage(e)); this.loading.set(false); },
    });
  }

  openForm(row?: BudgetVsActualRow): void {
    this.formError.set(null);
    this.form.set({
      departmentId: row?.departmentId ?? this.departments()?.[0]?.id ?? '',
      month: row?.month ?? this.month() ?? new Date().getUTCMonth() + 1,
      amount: row?.budget ?? 0,
    });
    this.formOpen.set(true);
  }

  setField(field: 'departmentId' | 'month' | 'amount', value: string): void {
    this.form.update((f) => ({ ...f, [field]: field === 'departmentId' ? value : Number(value) }));
  }

  /** Creates the month's budget, or updates it (with its current version) if one exists. */
  save(): void {
    const { departmentId, month, amount } = this.form();
    this.busy.set(true);
    this.formError.set(null);
    this.api.list(this.year()).subscribe({
      next: (budgets: Budget[]) => {
        const existing = budgets.find((b) => b.departmentId === departmentId && b.month === month);
        const request = existing ? this.api.update(existing, amount) : this.api.create(departmentId, this.year(), month, amount);
        request.subscribe({
          next: () => { this.busy.set(false); this.formOpen.set(false); this.load(); },
          error: (e: unknown) => { this.busy.set(false); this.formError.set(problemMessage(e)); },
        });
      },
      error: (e: unknown) => { this.busy.set(false); this.formError.set(problemMessage(e)); },
    });
  }

  private has(permission: string): boolean {
    return (this.user()?.permissions ?? []).includes(permission);
  }
}
