import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CurrentUser } from '../foundation.service';
import { Expenses } from './expenses';
import { ExpenseDetail, ExpensesService, problemMessage } from './expenses.service';

const detail = (overrides: Partial<ExpenseDetail> = {}): ExpenseDetail => ({
  id: 'e1', requesterId: 'u1', requesterName: 'Demo Employee', departmentId: 'd1', departmentName: 'Demo Engineering',
  amount: 42.5, currency: 'USD', category: 'Travel', description: 'Taxi', expenseDate: '2026-11-10', status: 'Submitted',
  createdAtUtc: '2026-11-10T00:00:00Z', submittedAtUtc: '2026-11-11T00:00:00Z', updatedAtUtc: '2026-11-11T00:00:00Z',
  decidedAtUtc: null, postedAtUtc: null, decisions: [], allowedActions: [], version: '"AAAAAAAAB9E="', ...overrides,
});

describe('ExpensesService', () => {
  let http: HttpTestingController;
  let service: ExpensesService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(ExpensesService);
  });
  afterEach(() => http.verify());

  it('sends the ETag as If-Match on transitions, with an optional comment', () => {
    service.act(detail(), 'reject', '  Needs a receipt ').subscribe();
    const request = http.expectOne('/api/expenses/e1/reject');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('If-Match')).toBe('"AAAAAAAAB9E="');
    expect(request.request.body).toEqual({ comment: 'Needs a receipt' });
    request.flush(detail({ status: 'Rejected' }));
  });

  it('sends If-Match on edits and no body on plain transitions', () => {
    service.update(detail({ status: 'Draft' }), { amount: 10, currency: 'USD', category: 'Meals', description: 'Lunch', expenseDate: '2026-11-10' }).subscribe();
    expect(http.expectOne('/api/expenses/e1').request.headers.get('If-Match')).toBe('"AAAAAAAAB9E="');
    service.act(detail(), 'approve').subscribe();
    expect(http.expectOne('/api/expenses/e1/approve').request.body).toBeNull();
  });

  it('builds list queries from filters', () => {
    service.list({ status: 'Submitted', sort: 'submittedAt', page: 2, pageSize: 10 }).subscribe();
    const request = http.expectOne((r) => r.url === '/api/expenses');
    expect(request.request.params.get('status')).toBe('Submitted');
    expect(request.request.params.get('sort')).toBe('submittedAt');
    expect(request.request.params.get('page')).toBe('2');
    expect(request.request.params.has('mine')).toBe(false);
    request.flush({ items: [], page: 2, pageSize: 10, totalCount: 0 });
  });
});

describe('problemMessage', () => {
  it('prefers validation errors, then detail', () => {
    expect(problemMessage(new HttpErrorResponse({ status: 400, error: { errors: { amount: ['Too small.'] } } }))).toBe('Too small.');
    expect(problemMessage(new HttpErrorResponse({ status: 412, error: { title: 'Expense was modified', detail: 'Reload it.' } }))).toBe('Reload it.');
    expect(problemMessage(new HttpErrorResponse({ status: 0 }))).toContain('unreachable');
  });
});

describe('Expenses panel', () => {
  const manager: CurrentUser = { displayName: 'M', email: 'm@example.invalid', roles: ['Manager'], departmentIds: ['d1'],
    permissions: ['expenses.create', 'expenses.read.department', 'expenses.approve'] };
  const employee: CurrentUser = { ...manager, roles: ['Employee'], permissions: ['expenses.create'] };

  async function render(user: CurrentUser, expense = detail()) {
    const api = {
      list: vi.fn().mockReturnValue(of({ items: [expense], page: 1, pageSize: 10, totalCount: 1 })),
      get: vi.fn().mockReturnValue(of(expense)),
      act: vi.fn().mockReturnValue(of({ ...expense, status: 'Approved', allowedActions: [] })),
    };
    await TestBed.configureTestingModule({ imports: [Expenses], providers: [{ provide: ExpensesService, useValue: api }] }).compileComponents();
    const fixture = TestBed.createComponent(Expenses);
    fixture.componentRef.setInput('user', user);
    fixture.componentRef.setInput('departments', [{ id: 'd1', name: 'Demo Engineering' }]);
    fixture.detectChanges();
    return { fixture, api, text: () => fixture.nativeElement.textContent as string };
  }

  it('shows tabs that match the role', async () => {
    const { text } = await render(employee);
    expect(text()).toContain('My expenses');
    expect(text()).not.toContain('Approval queue');
  });

  it('managers get the approval queue and only the server-allowed actions', async () => {
    const { fixture, api, text } = await render(manager, detail({ allowedActions: ['approve', 'reject'] }));
    expect(text()).toContain('Approval queue');
    fixture.nativeElement.querySelector('button.link').click();
    fixture.detectChanges();
    expect(text()).toContain('Approve');
    expect(text()).toContain('Reject');
    expect(text()).not.toContain('Post to ledger');

    const approve = [...fixture.nativeElement.querySelectorAll('button')].find((b: HTMLButtonElement) => b.textContent?.trim() === 'Approve') as HTMLButtonElement;
    approve.click();
    fixture.detectChanges();
    expect(api.act).toHaveBeenCalledWith(expect.objectContaining({ id: 'e1' }), 'approve', '');
    expect(text()).toContain('Approved');
  });

  it('says so when no actions are available', async () => {
    const { fixture, text } = await render(employee, detail({ allowedActions: [] }));
    fixture.nativeElement.querySelector('button.link').click();
    fixture.detectChanges();
    expect(text()).toContain('No actions available to you.');
  });
});
