import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CurrentUser } from '../foundation.service';
import { Budgets } from './budgets';
import { Budget, BudgetsService, BudgetVsActualRow } from './budgets.service';

const row = (overrides: Partial<BudgetVsActualRow> = {}): BudgetVsActualRow => ({
  departmentId: 'd1', departmentName: 'Demo Engineering', year: 2026, month: 11, budget: 1000, posted: 600,
  committed: 250, pending: 49, remaining: 150, utilizationPercent: 85, ...overrides,
});

const user = (...permissions: string[]): CurrentUser =>
  ({ displayName: 'U', email: 'u@example.invalid', roles: [], departmentIds: ['d1'], permissions });

describe('BudgetsService', () => {
  let http: HttpTestingController;
  let service: BudgetsService;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(BudgetsService);
  });
  afterEach(() => http.verify());

  it('sends If-Match when changing a budget', () => {
    const budget = { id: 'b1', version: '"AAAAAAAAB9E="' } as Budget;
    service.update(budget, 1200).subscribe();
    const request = http.expectOne('/api/budgets/b1');
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('If-Match')).toBe('"AAAAAAAAB9E="');
    expect(request.request.body).toEqual({ amount: 1200 });
  });

  it('omits month for whole-year reports', () => {
    service.report(2026).subscribe();
    const request = http.expectOne((r) => r.url === '/api/reports/budget-vs-actual');
    expect(request.request.params.get('year')).toBe('2026');
    expect(request.request.params.has('month')).toBe(false);
    request.flush([]);
  });
});

describe('Budgets panel', () => {
  async function render(current: CurrentUser, rows: BudgetVsActualRow[] = [row()]) {
    const api = { report: vi.fn().mockReturnValue(of(rows)), list: vi.fn(), create: vi.fn(), update: vi.fn() };
    await TestBed.configureTestingModule({ imports: [Budgets], providers: [{ provide: BudgetsService, useValue: api }] }).compileComponents();
    const fixture = TestBed.createComponent(Budgets);
    fixture.componentRef.setInput('user', current);
    fixture.componentRef.setInput('departments', [{ id: 'd1', name: 'Demo Engineering' }]);
    fixture.detectChanges();
    return { fixture, api, text: () => fixture.nativeElement.textContent as string };
  }

  it('is hidden from users without budget access', async () => {
    const { api, text } = await render(user('expenses.create'));
    expect(text().trim()).toBe('');
    expect(api.report).not.toHaveBeenCalled();
  });

  it('shows the report to managers without the edit controls', async () => {
    const { text } = await render(user('budgets.read.department'));
    expect(text()).toContain('Budget vs. actual');
    expect(text()).toContain('Demo Engineering');
    expect(text()).toContain('85.0%');
    expect(text()).not.toContain('Set budget');
  });

  it('lets finance set budgets and flags overspend', async () => {
    const { fixture, text } = await render(user('budgets.read.all', 'budgets.manage'),
      [row({ remaining: -50, utilizationPercent: 105 })]);
    expect(text()).toContain('Set budget');
    expect(fixture.nativeElement.querySelector('.bar.over')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('td.negative')).not.toBeNull();
  });

  it('labels spend with no budget', async () => {
    const { text } = await render(user('budgets.read.all'), [row({ budget: null, remaining: null, utilizationPercent: null })]);
    expect(text()).toContain('No budget');
  });
});
