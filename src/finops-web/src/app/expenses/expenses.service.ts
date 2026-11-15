import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export type ExpenseStatus = 'Draft' | 'Submitted' | 'Approved' | 'Rejected' | 'Posted' | 'Cancelled';
export type ExpenseAction = 'edit' | 'submit' | 'withdraw' | 'cancel' | 'revise' | 'approve' | 'reject' | 'post';

export const categories = ['Travel', 'Lodging', 'Meals', 'Supplies', 'Software', 'Training', 'Other'] as const;

export interface ExpenseSummary {
  id: string; requesterId: string; requesterName: string; departmentId: string; departmentName: string;
  amount: number; currency: string; category: string; description: string; expenseDate: string;
  status: ExpenseStatus; createdAtUtc: string; submittedAtUtc: string | null;
}

export interface ApprovalDecision { approverName: string; outcome: string; comment: string | null; decidedAtUtc: string; }

export interface ExpenseDetail extends ExpenseSummary {
  updatedAtUtc: string; decidedAtUtc: string | null; postedAtUtc: string | null;
  decisions: ApprovalDecision[]; allowedActions: ExpenseAction[]; version: string;
  /** Set by the background worker once the posted expense's reimbursement is scheduled. */
  reimbursementScheduledAtUtc?: string | null;
}

export interface Page<T> { items: T[]; page: number; pageSize: number; totalCount: number; }

export interface ExpenseFields {
  amount: number; currency: string; category: string; description: string; expenseDate: string;
}

export interface ListFilter { mine?: boolean; status?: ExpenseStatus; page?: number; pageSize?: number; sort?: string; }

/** Turns RFC 9457 Problem Details into one readable message for the UI. */
export function problemMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) return 'Something went wrong. Try again.';
  const body = error.error as { detail?: string; title?: string; errors?: Record<string, string[]> } | null;
  if (body?.errors) return Object.values(body.errors).flat().join(' ');
  if (body?.detail) return body.detail;
  if (error.status === 0) return 'The API is unreachable. Is it running?';
  return body?.title ?? `Request failed (${error.status}).`;
}

/**
 * Expense API client. Every write sends the ETag from the last read as If-Match, so two people
 * editing the same expense cannot silently overwrite each other (the API answers 412 instead).
 */
@Injectable({ providedIn: 'root' })
export class ExpensesService {
  private readonly http = inject(HttpClient);

  list(filter: ListFilter): Observable<Page<ExpenseSummary>> {
    let params = new HttpParams().set('page', filter.page ?? 1).set('pageSize', filter.pageSize ?? 25);
    if (filter.mine) params = params.set('mine', true);
    if (filter.status) params = params.set('status', filter.status);
    if (filter.sort) params = params.set('sort', filter.sort);
    return this.http.get<Page<ExpenseSummary>>('/api/expenses', { params });
  }

  get(id: string): Observable<ExpenseDetail> {
    return this.http.get<ExpenseDetail>(`/api/expenses/${id}`);
  }

  create(departmentId: string, fields: ExpenseFields): Observable<ExpenseDetail> {
    return this.http.post<ExpenseDetail>('/api/expenses', { departmentId, ...fields });
  }

  update(expense: ExpenseDetail, fields: ExpenseFields): Observable<ExpenseDetail> {
    return this.http.put<ExpenseDetail>(`/api/expenses/${expense.id}`, fields, { headers: this.ifMatch(expense) });
  }

  act(expense: ExpenseDetail, action: Exclude<ExpenseAction, 'edit'>, comment?: string): Observable<ExpenseDetail> {
    const body = comment?.trim() ? { comment: comment.trim() } : null;
    return this.http.post<ExpenseDetail>(`/api/expenses/${expense.id}/${action}`, body, { headers: this.ifMatch(expense) });
  }

  private ifMatch(expense: ExpenseDetail): HttpHeaders {
    return new HttpHeaders({ 'If-Match': expense.version });
  }
}
