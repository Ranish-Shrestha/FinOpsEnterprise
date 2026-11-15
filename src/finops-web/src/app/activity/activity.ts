import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { AuthService } from '../auth.service';
import { problemMessage } from '../expenses/expenses.service';
import { CurrentUser } from '../foundation.service';
import { ActivityService, AppNotification, notificationText, OutboxStatus } from './activity.service';

/**
 * Notifications written by the background worker, plus the outbox's health for administrators.
 * Notifications appear a moment after an action because the worker delivers them asynchronously.
 */
@Component({
  selector: 'app-activity',
  imports: [CurrencyPipe, DatePipe],
  templateUrl: './activity.html',
  styleUrl: './activity.css',
})
export class Activity {
  private readonly api = inject(ActivityService);
  private readonly auth = inject(AuthService);

  readonly user = input<CurrentUser | null>(null);

  readonly notifications = signal<AppNotification[]>([]);
  readonly unread = signal(0);
  readonly outbox = signal<OutboxStatus | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);

  readonly canOperate = computed(() => this.user()?.permissions.includes('messaging.manage') ?? false);
  readonly text = notificationText;

  constructor() {
    effect(() => {
      this.auth.persona();
      const user = this.user();
      untracked(() => {
        this.message.set(null);
        if (user) this.load();
        else { this.notifications.set([]); this.unread.set(0); this.outbox.set(null); }
      });
    });
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.inbox().subscribe({
      next: (inbox) => { this.notifications.set(inbox.items); this.unread.set(inbox.unreadCount); this.loading.set(false); },
      error: (e: unknown) => { this.error.set(problemMessage(e)); this.loading.set(false); },
    });
    if (this.canOperate()) {
      this.api.outbox().subscribe({
        next: (status) => this.outbox.set(status),
        error: (e: unknown) => this.error.set(problemMessage(e)),
      });
    } else {
      this.outbox.set(null);
    }
  }

  markRead(item: AppNotification): void {
    if (item.readAtUtc) return;
    this.api.markRead(item.id).subscribe({
      next: () => {
        this.notifications.update((items) => items.map((n) => n.id === item.id ? { ...n, readAtUtc: new Date().toISOString() } : n));
        this.unread.update((count) => Math.max(0, count - 1));
      },
      error: (e: unknown) => this.error.set(problemMessage(e)),
    });
  }

  replay(id: string): void {
    this.api.replay(id).subscribe({
      next: () => { this.load(); this.message.set('Message returned to the queue. The worker will deliver it shortly.'); },
      error: (e: unknown) => this.error.set(problemMessage(e)),
    });
  }
}
