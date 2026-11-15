import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export type NotificationKind = 'ExpenseApproved' | 'ExpenseRejected' | 'ExpensePosted';
export interface AppNotification {
  id: string; kind: NotificationKind; expenseId: string; expenseDescription: string; amount: number; currency: string;
  createdAtUtc: string; readAtUtc: string | null;
}
export interface NotificationInbox { unreadCount: number; items: AppNotification[]; }
export interface DeadLetter {
  id: string; type: string; deliveryCount: number; occurredAtUtc: string; deadLetteredAtUtc: string;
  lastError: string | null; correlationId: string | null;
}
export interface OutboxStatus { pending: number; deadLettered: number; deadLetters: DeadLetter[]; }

/** Notifications (always the caller's own; the server decides whose) and outbox operations (administrators). */
@Injectable({ providedIn: 'root' })
export class ActivityService {
  private readonly http = inject(HttpClient);

  inbox(): Observable<NotificationInbox> {
    return this.http.get<NotificationInbox>('/api/notifications');
  }

  markRead(id: string): Observable<void> {
    return this.http.post<void>(`/api/notifications/${id}/read`, null);
  }

  outbox(): Observable<OutboxStatus> {
    return this.http.get<OutboxStatus>('/api/messaging/outbox');
  }

  replay(id: string): Observable<void> {
    return this.http.post<void>(`/api/messaging/outbox/${id}/replay`, null);
  }
}

export function notificationText(n: Pick<AppNotification, 'kind'>): string {
  switch (n.kind) {
    case 'ExpenseApproved': return 'Your expense was approved';
    case 'ExpenseRejected': return 'Your expense was rejected. Open it to see why and revise it';
    case 'ExpensePosted': return 'Your expense was posted and a reimbursement is scheduled';
  }
}
