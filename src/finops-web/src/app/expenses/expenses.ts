import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Observable } from 'rxjs';
import { AuthService } from '../auth.service';
import { CurrentUser, Department } from '../foundation.service';
import {
  categories, ExpenseAction, ExpenseDetail, ExpenseFields, ExpensesService, ExpenseStatus, ExpenseSummary, ListFilter, problemMessage,
} from './expenses.service';

type View = 'mine' | 'queue' | 'toPost' | 'all';

interface Tab { view: View; label: string; filter: ListFilter; }

const actionLabels: Record<Exclude<ExpenseAction, 'edit'>, string> = {
  submit: 'Submit for approval', withdraw: 'Withdraw', cancel: 'Cancel expense', revise: 'Revise',
  approve: 'Approve', reject: 'Reject', post: 'Post to ledger',
};

function today(): string {
  return new Date().toISOString().slice(0, 10);
}

/**
 * Expense workflow panel. Which tabs and buttons appear is driven by the caller's permissions and by
 * the server's `allowedActions`; the API enforces every rule again, so hiding a button is not security.
 */
@Component({
  selector: 'app-expenses',
  imports: [CurrencyPipe, DatePipe],
  templateUrl: './expenses.html',
  styleUrl: './expenses.css',
})
export class Expenses {
  private readonly api = inject(ExpensesService);
  private readonly auth = inject(AuthService);

  readonly user = input<CurrentUser | null>(null);
  readonly departments = input<Department[] | null>(null);

  readonly categories = categories;
  readonly view = signal<View>('mine');
  readonly page = signal(1);
  readonly pageSize = 10;
  readonly loading = signal(false);
  readonly listError = signal<string | null>(null);
  readonly items = signal<ExpenseSummary[]>([]);
  readonly total = signal(0);

  readonly selected = signal<ExpenseDetail | null>(null);
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly comment = signal('');

  readonly formMode = signal<'closed' | 'create' | 'edit'>('closed');
  readonly form = signal<ExpenseFields & { departmentId: string }>(this.emptyForm());

  readonly tabs = computed<Tab[]>(() => {
    const permissions = this.user()?.permissions ?? [];
    const tabs: Tab[] = [{ view: 'mine', label: 'My expenses', filter: { mine: true } }];
    if (permissions.includes('expenses.approve')) tabs.push({ view: 'queue', label: 'Approval queue', filter: { status: 'Submitted', sort: 'submittedAt' } });
    if (permissions.includes('expenses.post')) tabs.push({ view: 'toPost', label: 'Ready to post', filter: { status: 'Approved' } });
    if (permissions.includes('expenses.read.department') || permissions.includes('expenses.read.all')) {
      tabs.push({ view: 'all', label: 'All visible', filter: {} });
    }
    return tabs;
  });

  readonly canCreate = computed(() => (this.user()?.permissions ?? []).includes('expenses.create'));
  /** Only departments the caller is an active member of; the API refuses any other. */
  readonly memberDepartments = computed(() => {
    const ids = new Set(this.user()?.departmentIds ?? []);
    return (this.departments() ?? []).filter((d) => ids.has(d.id));
  });
  readonly pageCount = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize)));
  readonly transitions = computed(() =>
    (this.selected()?.allowedActions ?? []).filter((a): a is Exclude<ExpenseAction, 'edit'> => a !== 'edit'));
  readonly needsComment = computed(() => this.transitions().some((a) => a === 'approve' || a === 'reject'));

  constructor() {
    // Reload whenever the persona or the user's access changes.
    effect(() => {
      this.auth.persona();
      const user = this.user();
      untracked(() => {
        if (!this.tabs().some((t) => t.view === this.view())) this.view.set('mine');
        this.closeDetail();
        this.formMode.set('closed');
        if (user) this.load(); else { this.items.set([]); this.total.set(0); }
      });
    });
  }

  label(action: Exclude<ExpenseAction, 'edit'>): string {
    return actionLabels[action];
  }

  statusClass(status: ExpenseStatus): string {
    return `status status-${status.toLowerCase()}`;
  }

  show(view: View): void {
    this.view.set(view);
    this.page.set(1);
    this.closeDetail();
    this.load();
  }

  goTo(page: number): void {
    this.page.set(page);
    this.load();
  }

  load(): void {
    const tab = this.tabs().find((t) => t.view === this.view()) ?? this.tabs()[0];
    this.loading.set(true);
    this.listError.set(null);
    this.api.list({ ...tab.filter, page: this.page(), pageSize: this.pageSize }).subscribe({
      next: (page) => { this.items.set(page.items); this.total.set(page.totalCount); this.loading.set(false); },
      error: (error: unknown) => { this.items.set([]); this.total.set(0); this.listError.set(problemMessage(error)); this.loading.set(false); },
    });
  }

  open(id: string): void {
    this.actionError.set(null);
    this.comment.set('');
    this.formMode.set('closed');
    this.api.get(id).subscribe({
      next: (expense) => this.selected.set(expense),
      error: (error: unknown) => this.actionError.set(problemMessage(error)),
    });
  }

  closeDetail(): void {
    this.selected.set(null);
    this.actionError.set(null);
    this.comment.set('');
  }

  act(action: Exclude<ExpenseAction, 'edit'>): void {
    const expense = this.selected();
    if (!expense) return;
    this.run(this.api.act(expense, action, this.comment()));
  }

  startCreate(): void {
    this.closeDetail();
    this.form.set(this.emptyForm());
    this.formMode.set('create');
  }

  startEdit(): void {
    const e = this.selected();
    if (!e) return;
    this.form.set({ departmentId: e.departmentId, amount: e.amount, currency: e.currency, category: e.category,
      description: e.description, expenseDate: e.expenseDate });
    this.formMode.set('edit');
  }

  setField(field: keyof ExpenseFields | 'departmentId', value: string): void {
    this.form.update((f) => ({ ...f, [field]: field === 'amount' ? Number(value) : value }));
  }

  save(): void {
    const { departmentId, ...fields } = this.form();
    const expense = this.selected();
    const request = this.formMode() === 'edit' && expense
      ? this.api.update(expense, fields)
      : this.api.create(departmentId, fields);
    this.run(request, () => this.formMode.set('closed'));
  }

  cancelForm(): void {
    this.formMode.set('closed');
    this.actionError.set(null);
  }

  private run(request: Observable<ExpenseDetail>, onSuccess?: () => void): void {
    this.busy.set(true);
    this.actionError.set(null);
    request.subscribe({
      next: (expense) => {
        this.selected.set(expense);
        this.comment.set('');
        this.busy.set(false);
        onSuccess?.();
        this.load();
      },
      error: (error: unknown) => {
        this.actionError.set(problemMessage(error));
        this.busy.set(false);
      },
    });
  }

  private emptyForm(): ExpenseFields & { departmentId: string } {
    return { departmentId: this.memberDepartments?.()[0]?.id ?? '', amount: 0, currency: 'USD', category: 'Travel',
      description: '', expenseDate: today() };
  }
}
