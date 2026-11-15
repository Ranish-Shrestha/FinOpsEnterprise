import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CurrentUser } from '../foundation.service';
import { Activity } from './activity';
import { ActivityService, AppNotification, notificationText, OutboxStatus } from './activity.service';

const user = (...permissions: string[]): CurrentUser =>
  ({ displayName: 'U', email: 'u@example.invalid', roles: [], departmentIds: [], permissions });

const notification = (overrides: Partial<AppNotification> = {}): AppNotification => ({
  id: 'n1', kind: 'ExpensePosted', expenseId: 'e1', expenseDescription: 'Train ticket', amount: 42.5, currency: 'USD',
  createdAtUtc: '2026-11-14T12:00:00Z', readAtUtc: null, ...overrides,
});

const outboxWithDeadLetter: OutboxStatus = {
  pending: 2, deadLettered: 1,
  deadLetters: [{ id: 'm1', type: 'expense.posted.v1', deliveryCount: 5, occurredAtUtc: '2026-11-14T12:00:00Z',
    deadLetteredAtUtc: '2026-11-14T12:10:00Z', lastError: 'TimeoutException: x', correlationId: 'c1' }],
};

describe('ActivityService', () => {
  let http: HttpTestingController;
  let service: ActivityService;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(ActivityService);
  });
  afterEach(() => http.verify());

  it('marks a notification read and replays a dead letter with POST', () => {
    service.markRead('n1').subscribe();
    const read = http.expectOne('/api/notifications/n1/read');
    expect(read.request.method).toBe('POST');
    read.flush(null);
    service.replay('m1').subscribe();
    const replay = http.expectOne('/api/messaging/outbox/m1/replay');
    expect(replay.request.method).toBe('POST');
    replay.flush(null);
  });

  it('describes every notification kind', () => {
    for (const kind of ['ExpenseApproved', 'ExpenseRejected', 'ExpensePosted'] as const)
      expect(notificationText({ kind }).length).toBeGreaterThan(10);
  });
});

describe('Activity panel', () => {
  async function render(current: CurrentUser | null, items: AppNotification[] = [notification()]) {
    const api = {
      inbox: vi.fn().mockReturnValue(of({ unreadCount: items.filter((n) => !n.readAtUtc).length, items })),
      markRead: vi.fn().mockReturnValue(of(undefined)),
      outbox: vi.fn().mockReturnValue(of(outboxWithDeadLetter)),
      replay: vi.fn().mockReturnValue(of(undefined)),
    };
    await TestBed.configureTestingModule({ imports: [Activity], providers: [{ provide: ActivityService, useValue: api }] }).compileComponents();
    const fixture = TestBed.createComponent(Activity);
    fixture.componentRef.setInput('user', current);
    fixture.detectChanges();
    return { fixture, api, text: () => fixture.nativeElement.textContent as string };
  }

  it('renders nothing without a provisioned user', async () => {
    const { api, text } = await render(null);
    expect(text().trim()).toBe('');
    expect(api.inbox).not.toHaveBeenCalled();
  });

  it('shows notifications and the unread count, and marks one read', async () => {
    const { fixture, api, text } = await render(user('expenses.create'));
    expect(text()).toContain('Your expense was posted');
    expect(text()).toContain('Train ticket');
    expect(fixture.nativeElement.querySelector('.count').textContent.trim()).toBe('1');
    fixture.nativeElement.querySelector('.notifications button').click();
    fixture.detectChanges();
    expect(api.markRead).toHaveBeenCalledWith('n1');
    expect(fixture.nativeElement.querySelector('.count')).toBeNull();
  });

  it('shows the outbox only to users who may operate it', async () => {
    const { api, text } = await render(user('expenses.create'));
    expect(api.outbox).not.toHaveBeenCalled();
    expect(text()).not.toContain('dead-lettered');
  });

  it('lets an operator replay a dead letter', async () => {
    const { fixture, api, text } = await render(user('messaging.manage'), []);
    expect(text()).toContain('2 pending');
    expect(text()).toContain('TimeoutException: x');
    fixture.nativeElement.querySelector('.dead-letters button').click();
    fixture.detectChanges();
    expect(api.replay).toHaveBeenCalledWith('m1');
    expect(text()).toContain('returned to the queue');
  });
});
