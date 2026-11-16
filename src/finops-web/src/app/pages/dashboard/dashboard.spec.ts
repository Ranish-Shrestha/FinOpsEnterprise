import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { ActivityService } from '../../activity/activity.service';
import { BudgetsService } from '../../budgets/budgets.service';
import { SessionStore } from '../../core/session.store';
import { ExpensesService, ListFilter } from '../../expenses/expenses.service';
import { CurrentUser, FoundationService } from '../../foundation.service';
import { Dashboard } from './dashboard';

const expense = (id: string, description: string, status = 'Submitted') => ({
  id, requesterId: 'r', requesterName: 'Demo Employee', departmentId: 'd', departmentName: 'Demo Engineering',
  amount: 120, currency: 'USD', category: 'Travel', description, expenseDate: '2026-11-02', status,
  createdAtUtc: '2026-11-02T10:00:00Z', submittedAtUtc: '2026-11-03T10:00:00Z',
});

function user(permissions: string[]): CurrentUser {
  return { displayName: 'Ranish Shrestha', email: 'r@example.invalid', roles: ['Manager'], permissions, departmentIds: ['d'] };
}

describe('Dashboard', () => {
  async function render(current: CurrentUser, overrides: { list?: (f: ListFilter) => unknown; budgets?: unknown } = {}) {
    const list = vi.fn(overrides.list ?? ((f: ListFilter) => of({
      items: f.status === 'Submitted' && !f.mine ? [expense('q1', 'Team offsite')] : [expense('m1', 'Taxi', 'Draft')],
      page: 1, pageSize: f.pageSize ?? 25, totalCount: f.mine ? (f.status === 'Draft' ? 2 : 1) : 3,
    })));
    const report = vi.fn(() => overrides.budgets ?? of([{ departmentId: 'd', departmentName: 'Demo Engineering', year: 2026,
      month: 11, budget: 1000, posted: 500, committed: 400, pending: 50, remaining: 100, utilizationPercent: 90 }]));
    await TestBed.configureTestingModule({
      imports: [Dashboard],
      providers: [
        provideRouter([]),
        { provide: FoundationService, useValue: { load: () => of({ apiOnline: true, databaseReady: true, departments: [{ id: 'd', name: 'Demo Engineering' }], user: current,
          signIn: { identityProvider: 'Google (direct sign-in)', methods: [], multiFactor: null, accountId: 'x' } }) } },
        { provide: ExpensesService, useValue: { list } },
        { provide: BudgetsService, useValue: { report } },
        { provide: ActivityService, useValue: { inbox: () => of({ unreadCount: 1, items: [
          { id: 'n1', kind: 'ExpenseApproved', expenseId: 'm1', expenseDescription: 'Hotel', amount: 300, currency: 'USD', createdAtUtc: '2026-11-04T09:00:00Z', readAtUtc: null },
        ] }) } },
      ],
    }).compileComponents();
    await TestBed.inject(SessionStore).refresh();
    const fixture = TestBed.createComponent(Dashboard);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, list, report, text: () => fixture.nativeElement.textContent as string };
  }

  it('greets the user and shows their numbers, approvals, budget and updates', async () => {
    const { text, fixture } = await render(user(['expenses.create', 'expenses.approve', 'budgets.read.department']));
    expect(text()).toContain('Ranish');
    const kpis = [...fixture.nativeElement.querySelectorAll('.kpi')].map((k: HTMLElement) => k.textContent);
    expect(kpis.some((k) => k?.includes('My drafts') && k.includes('2'))).toBe(true);
    expect(kpis.some((k) => k?.includes('To approve') && k.includes('3'))).toBe(true);
    expect(text()).toContain('Team offsite');
    expect(text()).toContain('90% used');
    expect(text()).toContain('Hotel');
    expect(text()).toContain('Google (direct sign-in)');
    expect(fixture.nativeElement.querySelector('a.button')?.textContent).toContain('New expense');
  });

  it('skips what the role cannot see instead of calling the API for a 403', async () => {
    const { text, list, report } = await render(user(['expenses.create']));
    expect(text()).not.toContain('To approve');
    expect(text()).not.toContain('Budget ·');
    expect(report).not.toHaveBeenCalled();
    expect(list.mock.calls.every(([f]) => f.mine)).toBe(true);
  });

  it('keeps the rest of the dashboard when one part fails', async () => {
    const { text } = await render(user(['budgets.read.department']), {
      budgets: throwError(() => new Error('boom')),
    });
    expect(text()).toContain("Couldn't load budgets.");
    expect(text()).toContain('Taxi');
  });
});
