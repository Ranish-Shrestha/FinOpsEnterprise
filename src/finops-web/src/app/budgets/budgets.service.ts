import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface BudgetVsActualRow {
  departmentId: string; departmentName: string; year: number; month: number;
  budget: number | null; posted: number; committed: number; pending: number;
  remaining: number | null; utilizationPercent: number | null;
}

export interface Budget {
  id: string; departmentId: string; departmentName: string; year: number; month: number;
  amount: number; currency: string; updatedAtUtc: string; version: string;
}

/** Budgets API. Updates send If-Match so two finance users cannot overwrite each other's change. */
@Injectable({ providedIn: 'root' })
export class BudgetsService {
  private readonly http = inject(HttpClient);

  report(year: number, month?: number): Observable<BudgetVsActualRow[]> {
    let params = new HttpParams().set('year', year);
    if (month) params = params.set('month', month);
    return this.http.get<BudgetVsActualRow[]>('/api/budgets/vs-actual', { params });
  }

  list(year: number): Observable<Budget[]> {
    return this.http.get<Budget[]>('/api/budgets', { params: new HttpParams().set('year', year) });
  }

  create(departmentId: string, year: number, month: number, amount: number): Observable<Budget> {
    return this.http.post<Budget>('/api/budgets', { departmentId, year, month, amount });
  }

  update(budget: Budget, amount: number): Observable<Budget> {
    return this.http.put<Budget>(`/api/budgets/${budget.id}`, { amount }, { headers: new HttpHeaders({ 'If-Match': budget.version }) });
  }
}
