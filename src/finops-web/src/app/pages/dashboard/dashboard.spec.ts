import { TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { SessionStore } from '../../core/session.store';
import { FoundationService, FoundationState } from '../../foundation.service';
import { Dashboard } from './dashboard';

describe('Dashboard', () => {
  async function render(load: () => unknown) {
    await TestBed.configureTestingModule({ imports: [Dashboard], providers: [{ provide: FoundationService, useValue: { load } }] }).compileComponents();
    const fixture = TestBed.createComponent(Dashboard);
    return { fixture, session: TestBed.inject(SessionStore) };
  }

  it('renders unavailable departments and offers a retry', async () => {
    const load = vi.fn().mockReturnValue(of({ apiOnline: true, databaseReady: false, departments: null, user: null, signIn: null }));
    const { fixture, session } = await render(load);
    await session.refresh();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain("We couldn't load the departments.");
    fixture.nativeElement.querySelector('button').click();
    expect(load).toHaveBeenCalledTimes(2);
  });

  it('shows loading before rendering real results', async () => {
    const result = new Subject<FoundationState>();
    const { fixture, session } = await render(() => result);
    const loaded = session.refresh();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('button').disabled).toBe(true);
    result.next({ apiOnline: true, databaseReady: true, departments: [{ id: 'test', name: 'Demo Finance' }], user: null, signIn: null });
    result.complete();
    await loaded;
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Demo Finance');
    expect(fixture.nativeElement.querySelector('button').disabled).toBe(false);
  });
});
